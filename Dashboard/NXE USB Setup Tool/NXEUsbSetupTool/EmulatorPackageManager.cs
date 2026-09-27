using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using NxeDashboard.Shared;

namespace NXEUsbSetupTool
{
    public sealed class EmulatorPackageFailure
    {
        public EmulatorPackageDefinition Definition { get; set; }
        public string Error { get; set; }
    }

    public sealed class EmulatorPackagePreparationResult
    {
        public Dictionary<string, EmulatorPackageInspection> Ready { get; } =
            new Dictionary<string, EmulatorPackageInspection>(StringComparer.OrdinalIgnoreCase);
        public List<EmulatorPackageFailure> Failures { get; } = new List<EmulatorPackageFailure>();
        public bool DownloadDeclined { get; set; }
    }

    public sealed class EmulatorPackageManager : IDisposable
    {
        private static readonly HashSet<string> CandidateExtensions = new HashSet<string>(
            new[] { ".appx", ".msix", ".appxbundle", ".msixbundle" },
            StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> NeverTraverseNames = new HashSet<string>(
            new[] { "System Volume Information", "$Recycle.Bin", "Recovery" },
            StringComparer.OrdinalIgnoreCase);

        private readonly EmulatorPackageInspector inspector;
        private readonly HttpClient httpClient;
        private readonly Action<string> logger;
        private readonly Action<int, string> progress;
        private readonly string[] searchRootsOverride;
        private readonly Func<EmulatorPackageDefinition, string, string> packageProvider;

        public EmulatorPackageManager(Action<string> logger, Action<int, string> progress,
            IEnumerable<string> searchRootsOverride = null,
            Func<EmulatorPackageDefinition, string, string> packageProvider = null)
        {
            inspector = new EmulatorPackageInspector();
            this.logger = logger ?? (_ => { });
            this.progress = progress ?? ((_, __) => { });
            this.searchRootsOverride = searchRootsOverride == null ? null : searchRootsOverride.ToArray();
            this.packageProvider = packageProvider;
            httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("NXE-USB-Setup-Tool/1.1");
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        public EmulatorPackagePreparationResult Prepare(
            string driveRoot,
            Func<bool> cancellationRequested,
            Func<int, bool> askToDownload,
            Func<IReadOnlyList<EmulatorPackageFailure>, bool> askToRetry)
        {
            cancellationRequested = cancellationRequested ?? (() => false);
            var result = new EmulatorPackagePreparationResult();
            logger("[EMU-PKG] scan started");
            progress(32, "Checking emulator packages...");

            var best = FindLocalPackages(driveRoot, cancellationRequested);
            ThrowIfCancelled(cancellationRequested);
            foreach (var pair in best)
            {
                var copied = CopyAtomically(pair.Value, driveRoot);
                result.Ready[pair.Key] = copied;
            }

            var missing = EmulatorPackageManifest.Definitions
                .Where(definition => !result.Ready.ContainsKey(definition.Id))
                .ToList();
            foreach (var definition in missing)
                logger("[EMU-PKG] " + definition.Id + " local package not found");
            if (missing.Count == 0)
                return result;

            if (!askToDownload(result.Ready.Count))
            {
                result.DownloadDeclined = true;
                logger("[EMU-PKG] download declined for this setup run");
                return result;
            }

            var pending = missing;
            while (pending.Count > 0)
            {
                ThrowIfCancelled(cancellationRequested);
                var failures = DownloadMissing(pending, driveRoot, result, cancellationRequested);
                result.Failures.Clear();
                result.Failures.AddRange(failures);
                if (failures.Count == 0)
                    break;
                if (!askToRetry(failures))
                    break;
                pending = failures.Select(failure => failure.Definition).ToList();
            }
            return result;
        }

        public Dictionary<string, EmulatorPackageInspection> FindLocalPackages(
            string driveRoot, Func<bool> cancellationRequested)
        {
            var best = new Dictionary<string, EmulatorPackageInspection>(StringComparer.OrdinalIgnoreCase);
            var scannedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scannedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fastRoots = (searchRootsOverride ?? GetFastSearchRoots(driveRoot).ToArray())
                .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var root in fastRoots)
            {
                ScanTree(root, best, scannedFiles, scannedDirectories, cancellationRequested);
            }

            if (best.Count == EmulatorPackageManifest.Definitions.Length)
                return best;

            if (searchRootsOverride != null)
                return best;

            progress(36, "Searching local drives for emulator packages...");
            foreach (var drive in DriveInfo.GetDrives())
            {
                ThrowIfCancelled(cancellationRequested);
                try
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                        continue;
                    ScanTree(drive.RootDirectory.FullName, best, scannedFiles, scannedDirectories, cancellationRequested);
                }
                catch (Exception ex)
                {
                    logger("[EMU-PKG] skipped drive=" + drive.Name + " reason=" + ex.Message);
                }
            }
            return best;
        }

        private void ScanTree(string root, Dictionary<string, EmulatorPackageInspection> best,
            HashSet<string> scannedFiles, HashSet<string> scannedDirectories,
            Func<bool> cancellationRequested)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                ThrowIfCancelled(cancellationRequested);
                var directory = pending.Pop();
                string fullDirectory;
                try { fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar); }
                catch { continue; }
                if (!scannedDirectories.Add(fullDirectory))
                    continue;
                IEnumerable<string> files;
                try
                {
                    files = new[] { "*.appx", "*.msix", "*.appxbundle", "*.msixbundle" }
                        .SelectMany(pattern => Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                        .ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    ThrowIfCancelled(cancellationRequested);
                    string fullPath;
                    try { fullPath = Path.GetFullPath(file); }
                    catch { continue; }
                    if (!scannedFiles.Add(fullPath))
                        continue;
                    logger("[EMU-PKG] candidate=" + fullPath);
                    var inspection = inspector.Inspect(fullPath);
                    if (!inspection.IsValid)
                    {
                        logger("[EMU-PKG] rejected=" + fullPath + " reason=" + inspection.Error);
                        continue;
                    }
                    logger("[EMU-PKG] identity=" + inspection.IdentityName);
                    logger("[EMU-PKG] architecture=" + inspection.Architecture);
                    logger("[EMU-PKG] matched=" + inspection.Definition.Id);
                    EmulatorPackageInspection existing;
                    if (!best.TryGetValue(inspection.Definition.Id, out existing) || IsBetter(inspection, existing))
                        best[inspection.Definition.Id] = inspection;
                }

                IEnumerable<string> directories;
                try { directories = Directory.EnumerateDirectories(directory).ToArray(); }
                catch { continue; }
                foreach (var child in directories)
                {
                    try
                    {
                        if (NeverTraverseNames.Contains(Path.GetFileName(child)))
                            continue;
                        var attributes = File.GetAttributes(child);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            continue;
                        pending.Push(child);
                    }
                    catch { }
                }
            }
        }

        private static bool IsBetter(EmulatorPackageInspection candidate, EmulatorPackageInspection current)
        {
            var versionComparison = candidate.Version.CompareTo(current.Version);
            if (versionComparison != 0)
                return versionComparison > 0;
            var candidateCanonical = string.Equals(Path.GetFileName(candidate.FilePath),
                candidate.Definition.CanonicalDestinationFileName, StringComparison.OrdinalIgnoreCase);
            var currentCanonical = string.Equals(Path.GetFileName(current.FilePath),
                current.Definition.CanonicalDestinationFileName, StringComparison.OrdinalIgnoreCase);
            return candidateCanonical && !currentCanonical;
        }

        private List<EmulatorPackageFailure> DownloadMissing(
            IReadOnlyList<EmulatorPackageDefinition> missing,
            string driveRoot,
            EmulatorPackagePreparationResult result,
            Func<bool> cancellationRequested)
        {
            var failures = new List<EmulatorPackageFailure>();
            var stagingRoot = Path.Combine(Path.GetTempPath(), "NXEUsbSetup", "EmulatorDownloads", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingRoot);
            try
            {
                for (var index = 0; index < missing.Count; index++)
                {
                    var definition = missing[index];
                    ThrowIfCancelled(cancellationRequested);
                    progress(40 + (int)(15.0 * index / Math.Max(1, missing.Count)),
                        "Downloading " + definition.DisplayName + "...");
                    try
                    {
                        var stagedPath = StageDownload(definition, stagingRoot, cancellationRequested);
                        var inspection = inspector.Inspect(stagedPath);
                        if (!inspection.IsValid || !string.Equals(inspection.Definition.Id, definition.Id, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(inspection.Error ?? "Downloaded package does not match the requested emulator.");
                        logger("[EMU-PKG] validation=pass emulator=" + definition.Id);
                        var copied = CopyAtomically(inspection, driveRoot);
                        result.Ready[definition.Id] = copied;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        logger("[EMU-PKG] download failed emulator=" + definition.Id + " reason=" + ex.Message);
                        failures.Add(new EmulatorPackageFailure { Definition = definition, Error = ex.Message });
                    }
                }
            }
            finally
            {
                try { Directory.Delete(stagingRoot, true); } catch { }
            }
            return failures;
        }

        private string StageDownload(EmulatorPackageDefinition definition, string stagingRoot,
            Func<bool> cancellationRequested)
        {
            if (packageProvider != null)
                return packageProvider(definition, stagingRoot);

            if (definition.SourceType == EmulatorPackageSourceType.ApprovedEmbeddedArtifact)
            {
                var output = Path.Combine(stagingRoot, definition.CanonicalDestinationFileName);
                using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(definition.EmbeddedResourceName))
                {
                    if (resource == null)
                        throw new FileNotFoundException("NXE-approved embedded package is unavailable.");
                    using (var file = File.Create(output))
                        resource.CopyTo(file);
                }
                logger("[EMU-PKG] download source=NXE approved embedded artifact emulator=" + definition.Id);
                return output;
            }

            var asset = ResolveGitHubAsset(definition);
            logger("[EMU-PKG] download source=" + asset.DownloadUrl);
            var destination = Path.Combine(stagingRoot, SanitizeFileName(asset.Name));
            using (var response = httpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using (var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var output = File.Create(destination))
                {
                    var buffer = new byte[1024 * 128];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ThrowIfCancelled(cancellationRequested);
                        output.Write(buffer, 0, read);
                    }
                }
            }
            if (new FileInfo(destination).Length == 0)
                throw new InvalidDataException("Downloaded file is empty.");
            logger("[EMU-PKG] downloaded=" + destination);
            return destination;
        }

        private GitHubAsset ResolveGitHubAsset(EmulatorPackageDefinition definition)
        {
            var apiUrl = "https://api.github.com/repos/" + definition.GitHubRepository + "/releases/latest";
            var json = httpClient.GetStringAsync(apiUrl).GetAwaiter().GetResult();
            var release = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (release == null || !release.ContainsKey("assets"))
                throw new InvalidDataException("GitHub release metadata did not contain assets.");
            var assets = release["assets"] as object[];
            if (assets == null)
                throw new InvalidDataException("GitHub release metadata assets were invalid.");

            var matches = new List<GitHubAsset>();
            foreach (var value in assets)
            {
                var asset = value as Dictionary<string, object>;
                if (asset == null) continue;
                var name = Value(asset, "name");
                var url = Value(asset, "browser_download_url");
                if (!CandidateExtensions.Contains(Path.GetExtension(name))) continue;
                if (definition.RequiredAssetNameParts != null && definition.RequiredAssetNameParts.Any(
                    part => name.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                if (definition.RejectedAssetNameParts != null && definition.RejectedAssetNameParts.Any(
                    part => name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (!string.IsNullOrWhiteSpace(url))
                    matches.Add(new GitHubAsset { Name = name, DownloadUrl = url });
            }
            if (matches.Count != 1)
                throw new InvalidDataException("Expected exactly one approved UWP asset but found " + matches.Count + ".");
            return matches[0];
        }

        private EmulatorPackageInspection CopyAtomically(EmulatorPackageInspection source, string driveRoot)
        {
            var destinationDirectory = Path.Combine(driveRoot, EmulatorPackageManifest.PackageRelativeDirectory);
            Directory.CreateDirectory(destinationDirectory);
            var destination = Path.Combine(destinationDirectory, source.Definition.CanonicalDestinationFileName);
            if (string.Equals(Path.GetFullPath(source.FilePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                return source;

            var temporary = Path.Combine(destinationDirectory,
                Path.GetFileNameWithoutExtension(destination) + ".nxe-" + Guid.NewGuid().ToString("N") +
                Path.GetExtension(destination));
            try
            {
                File.Copy(source.FilePath, temporary, true);
                var copiedInspection = inspector.Inspect(temporary);
                if (!copiedInspection.IsValid ||
                    !string.Equals(copiedInspection.Definition.Id, source.Definition.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(copiedInspection.Error ?? "Copied package did not pass re-validation.");
                if (File.Exists(destination))
                    File.Replace(temporary, destination, null, true);
                else
                    File.Move(temporary, destination);
                copiedInspection.FilePath = destination;
                logger("[EMU-PKG] copied=" + destination);
                return copiedInspection;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static IEnumerable<string> GetFastSearchRoots(string driveRoot)
        {
            yield return Path.Combine(driveRoot, EmulatorPackageManifest.PackageRelativeDirectory);
            yield return Path.Combine(driveRoot, "Cache");
            yield return Path.Combine(driveRoot, "NXE", EmulatorPackageManifest.PackageRelativeDirectory);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads";
            yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            yield return AppDomain.CurrentDomain.BaseDirectory;
            yield return Environment.CurrentDirectory;

            var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "Dashboard")))
                {
                    yield return current.FullName;
                    yield return Path.Combine(current.FullName, "Release");
                    yield break;
                }
                current = current.Parent;
            }
        }

        private static void ThrowIfCancelled(Func<bool> cancellationRequested)
        {
            if (cancellationRequested())
                throw new OperationCanceledException();
        }

        private static string Value(Dictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary.TryGetValue(key, out value) && value != null ? value.ToString() : string.Empty;
        }

        private static string SanitizeFileName(string value)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return value;
        }

        public void Dispose()
        {
            httpClient.Dispose();
        }

        private sealed class GitHubAsset
        {
            public string Name { get; set; }
            public string DownloadUrl { get; set; }
        }
    }
}
