using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NXEUsbSetupTool;
using NxeDashboard.Shared;

internal static class Program
{
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "NXEUsbSetupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var library = Path.Combine(root, "library");
            Directory.CreateDirectory(library);
            var packages = EmulatorPackageManifest.Definitions.ToDictionary(
                definition => definition.Id,
                definition => CreatePackage(library, definition, "x64"),
                StringComparer.OrdinalIgnoreCase);

            TestAllLocalAndSecondRun(root, packages);
            TestNoPackagesNoDownload(root);
            TestNoPackagesYesDownload(root, packages);
            TestPartialSet(root, packages);
            TestWrongArchitecture(root);
            TestDesktopBuildIgnored(root);
            TestBadDownloadPreservesDestination(root, packages);
            TestApprovedEmbeddedXenia(root, packages);

            Console.WriteLine("PASS: all 8 emulator-package acceptance scenarios");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void TestAllLocalAndSecondRun(string root, Dictionary<string, string> packages)
    {
        var source = CopySet(root, "all-local", packages, packages.Keys);
        var drive = NewDrive(root, "drive-all-local");
        var prompts = 0;
        using (var manager = Manager(new[] { source }))
        {
            var result = manager.Prepare(drive, () => false, _ => { prompts++; return false; }, _ => false);
            Assert(result.Ready.Count == 5, "Test 1: all local packages should be ready.");
            Assert(prompts == 0, "Test 1: all local packages must produce zero prompts.");
        }

        using (var manager = Manager(new[] { Path.Combine(drive, "Transfers") }))
        {
            var result = manager.Prepare(drive, () => false, _ => { prompts++; return false; }, _ => false);
            Assert(result.Ready.Count == 5, "Test 8: second run should reuse all destination packages.");
            Assert(prompts == 0, "Test 8: second run must produce zero prompts.");
        }
    }

    private static void TestNoPackagesNoDownload(string root)
    {
        var empty = Path.Combine(root, "empty-no");
        Directory.CreateDirectory(empty);
        var drive = NewDrive(root, "drive-no");
        var prompts = 0;
        using (var manager = Manager(new[] { empty }))
        {
            var result = manager.Prepare(drive, () => false, found => { prompts++; Assert(found == 0, "Test 3 count"); return false; }, _ => false);
            Assert(prompts == 1 && result.DownloadDeclined, "Test 3: No should prompt once and continue without downloads.");
            Assert(!Directory.EnumerateFiles(Path.Combine(drive, "Transfers")).Any(), "Test 3: No must download nothing.");
        }
    }

    private static void TestNoPackagesYesDownload(string root, Dictionary<string, string> packages)
    {
        var empty = Path.Combine(root, "empty-yes");
        Directory.CreateDirectory(empty);
        var drive = NewDrive(root, "drive-yes");
        var prompts = 0;
        using (var manager = Manager(new[] { empty }, Provider(packages)))
        {
            var result = manager.Prepare(drive, () => false, found => { prompts++; Assert(found == 0, "Test 2 count"); return true; }, _ => false);
            Assert(prompts == 1 && result.Ready.Count == 5 && result.Failures.Count == 0,
                "Test 2: Yes should stage, validate, and copy all missing packages.");
        }
    }

    private static void TestPartialSet(string root, Dictionary<string, string> packages)
    {
        var source = CopySet(root, "partial", packages, new[] { "xenia", "dolphin" });
        var drive = NewDrive(root, "drive-partial");
        var provided = new List<string>();
        var provider = new Func<EmulatorPackageDefinition, string, string>((definition, staging) =>
        {
            provided.Add(definition.Id);
            return CopyProviderFile(packages[definition.Id], definition, staging);
        });
        using (var manager = Manager(new[] { source }, provider))
        {
            var result = manager.Prepare(drive, () => false, found => { Assert(found == 2, "Test 4 count"); return true; }, _ => false);
            Assert(result.Ready.Count == 5, "Test 4: partial set should complete.");
            Assert(provided.OrderBy(x => x).SequenceEqual(new[] { "flycast", "retroarch", "xbsx2" }),
                "Test 4: only missing packages should be downloaded.");
        }
    }

    private static void TestWrongArchitecture(string root)
    {
        var definition = EmulatorPackageManifest.Find("xenia");
        var path = CreatePackage(root, definition, "x86", "wrong-architecture.appx");
        Assert(!new EmulatorPackageInspector().Inspect(path).IsValid,
            "Test 5: x86 package must be rejected.");
    }

    private static void TestDesktopBuildIgnored(string root)
    {
        var zip = Path.Combine(root, "xenia-desktop.zip");
        File.WriteAllText(zip, "not a UWP package");
        Assert(!new EmulatorPackageInspector().Inspect(zip).IsValid,
            "Test 6: desktop ZIP must be ignored.");
    }

    private static void TestBadDownloadPreservesDestination(string root, Dictionary<string, string> packages)
    {
        var empty = Path.Combine(root, "empty-bad");
        Directory.CreateDirectory(empty);
        var drive = NewDrive(root, "drive-bad");
        var dolphin = EmulatorPackageManifest.Find("dolphin");
        var destination = Path.Combine(drive, "Transfers", dolphin.CanonicalDestinationFileName);
        var sentinel = Encoding.UTF8.GetBytes("existing destination must survive");
        File.WriteAllBytes(destination, sentinel);
        var provider = new Func<EmulatorPackageDefinition, string, string>((definition, staging) =>
        {
            if (definition.Id == "dolphin")
            {
                var corrupt = Path.Combine(staging, definition.CanonicalDestinationFileName);
                File.WriteAllText(corrupt, "truncated");
                return corrupt;
            }
            return CopyProviderFile(packages[definition.Id], definition, staging);
        });
        var failureShown = false;
        using (var manager = Manager(new[] { empty }, provider))
        {
            var result = manager.Prepare(drive, () => false, _ => true, failures =>
            {
                failureShown = failures.Any(failure => failure.Definition.Id == "dolphin");
                return false;
            });
            Assert(failureShown && result.Failures.Count == 1, "Test 7: bad download should offer Retry/Continue.");
            Assert(File.ReadAllBytes(destination).SequenceEqual(sentinel),
                "Test 7: corrupt download must not replace an existing destination file.");
        }
    }

    private static void TestApprovedEmbeddedXenia(string root, Dictionary<string, string> packages)
    {
        var withoutXenia = CopySet(root, "embedded-xenia", packages,
            packages.Keys.Where(id => !string.Equals(id, "xenia", StringComparison.OrdinalIgnoreCase)));
        var drive = NewDrive(root, "drive-embedded-xenia");
        using (var manager = Manager(new[] { withoutXenia }))
        {
            var result = manager.Prepare(drive, () => false, found => found == 4, _ => false);
            Assert(result.Ready.ContainsKey("xenia") && result.Ready["xenia"].Version == new Version(1, 1, 5, 2),
                "Approved Xenia 1.1.5.2 embedded artifact should validate and stage.");
        }
    }

    private static EmulatorPackageManager Manager(IEnumerable<string> roots,
        Func<EmulatorPackageDefinition, string, string> provider = null) =>
        new EmulatorPackageManager(_ => { }, (_, __) => { }, roots, provider);

    private static Func<EmulatorPackageDefinition, string, string> Provider(Dictionary<string, string> packages) =>
        (definition, staging) => CopyProviderFile(packages[definition.Id], definition, staging);

    private static string CopyProviderFile(string source, EmulatorPackageDefinition definition, string staging)
    {
        var output = Path.Combine(staging, definition.CanonicalDestinationFileName);
        File.Copy(source, output, true);
        return output;
    }

    private static string CopySet(string root, string name, Dictionary<string, string> packages, IEnumerable<string> ids)
    {
        var output = Path.Combine(root, name);
        Directory.CreateDirectory(output);
        foreach (var id in ids)
            File.Copy(packages[id], Path.Combine(output, Path.GetFileName(packages[id])), true);
        return output;
    }

    private static string NewDrive(string root, string name)
    {
        var drive = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(drive, "Transfers"));
        return drive;
    }

    private static string CreatePackage(string directory, EmulatorPackageDefinition definition,
        string architecture, string fileName = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName ?? definition.CanonicalDestinationFileName);
        if (Path.GetExtension(path).Equals(".msixbundle", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".appxbundle", StringComparison.OrdinalIgnoreCase))
            CreateBundle(path, definition, architecture);
        else
            CreateAppPackage(path, definition, architecture);
        return path;
    }

    private static void CreateBundle(string path, EmulatorPackageDefinition definition, string architecture)
    {
        byte[] nested;
        using (var memory = new MemoryStream())
        {
            CreateAppPackage(memory, definition, architecture);
            nested = memory.ToArray();
        }
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(archive, "AppxMetadata/AppxBundleManifest.xml",
                "<Bundle><Packages><Package Type=\"application\" Architecture=\"" + architecture +
                "\" FileName=\"payload.msix\" /></Packages></Bundle>");
            Add(archive, "AppxSignature.p7x", "test-signature");
            var entry = archive.CreateEntry("payload.msix");
            using (var output = entry.Open()) output.Write(nested, 0, nested.Length);
        }
    }

    private static void CreateAppPackage(string path, EmulatorPackageDefinition definition, string architecture)
    {
        using (var stream = File.Create(path))
            CreateAppPackage(stream, definition, architecture);
    }

    private static void CreateAppPackage(Stream stream, EmulatorPackageDefinition definition, string architecture)
    {
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var version = definition.MinimumVersion ?? new Version(9, 9, 9, 9);
            Add(archive, "AppxManifest.xml",
                "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" " +
                "xmlns:uap=\"http://schemas.microsoft.com/appx/manifest/uap/windows10\">" +
                "<Identity Name=\"" + definition.AcceptedPackageIdentities[0] + "\" Publisher=\"" +
                definition.AcceptedPublishers[0] + "\" Version=\"" + version +
                "\" ProcessorArchitecture=\"" + architecture + "\" />" +
                "<Applications><Application Id=\"App\"><Extensions><uap:Extension Category=\"windows.protocol\">" +
                "<uap:Protocol Name=\"" + definition.ExpectedProtocol + "\" />" +
                "</uap:Extension></Extensions></Application></Applications></Package>");
            Add(archive, "AppxSignature.p7x", "test-signature");
        }
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using (var writer = new StreamWriter(entry.Open(), Encoding.UTF8)) writer.Write(content);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
