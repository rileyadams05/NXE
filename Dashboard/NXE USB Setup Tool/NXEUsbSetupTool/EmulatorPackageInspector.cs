using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using NxeDashboard.Shared;

namespace NXEUsbSetupTool
{
    public sealed class EmulatorPackageInspection
    {
        public string FilePath { get; set; }
        public EmulatorPackageDefinition Definition { get; set; }
        public string IdentityName { get; set; }
        public string Publisher { get; set; }
        public Version Version { get; set; }
        public string Architecture { get; set; }
        public IReadOnlyList<string> Protocols { get; set; }
        public bool IsBundle { get; set; }
        public bool SignaturePresent { get; set; }
        public string Error { get; set; }
        public bool IsValid => Definition != null && string.IsNullOrEmpty(Error);
    }

    public sealed class EmulatorPackageInspector
    {
        private static readonly HashSet<string> PackageExtensions = new HashSet<string>(
            new[] { ".appx", ".msix", ".appxbundle", ".msixbundle" },
            StringComparer.OrdinalIgnoreCase);

        public EmulatorPackageInspection Inspect(string path)
        {
            var result = new EmulatorPackageInspection { FilePath = path };
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return Invalid(result, "Package file does not exist.");
                if (!PackageExtensions.Contains(Path.GetExtension(path)))
                    return Invalid(result, "Unsupported package extension.");
                if (new FileInfo(path).Length <= 0)
                    return Invalid(result, "Package file is empty.");

                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                {
                    if (IsBundleExtension(path))
                        InspectBundle(archive, result);
                    else
                        InspectApplicationPackage(archive, result);
                }

                if (!string.IsNullOrEmpty(result.Error))
                    return result;

                result.Definition = EmulatorPackageManifest.Definitions.FirstOrDefault(definition =>
                    definition.AcceptedPackageIdentities.Any(identity =>
                        string.Equals(identity, result.IdentityName, StringComparison.OrdinalIgnoreCase)));
                if (result.Definition == null)
                    return Invalid(result, "Package identity is not supported by NXE.");
                if (!string.Equals(result.Architecture, result.Definition.Architecture, StringComparison.OrdinalIgnoreCase))
                    return Invalid(result, "Package architecture is not x64.");
                if (result.Definition.MinimumVersion != null && result.Version.CompareTo(result.Definition.MinimumVersion) < 0)
                    return Invalid(result, "Package version is older than the NXE-approved minimum.");
                if (result.Definition.AcceptedPublishers != null && result.Definition.AcceptedPublishers.Count > 0 &&
                    !result.Definition.AcceptedPublishers.Any(publisher =>
                        string.Equals(publisher, result.Publisher, StringComparison.OrdinalIgnoreCase)))
                    return Invalid(result, "Package publisher does not match the approved publisher.");
                if (!result.Protocols.Any(protocol => string.Equals(
                    protocol.TrimEnd(':'), result.Definition.ExpectedProtocol.TrimEnd(':'),
                    StringComparison.OrdinalIgnoreCase)))
                    return Invalid(result, "Expected emulator protocol registration was not found.");
                var candidateName = Path.GetFileName(path);
                if (result.Definition.RequireAssetMarkersDuringInspection &&
                    result.Definition.RequiredAssetNameParts != null &&
                    result.Definition.RequiredAssetNameParts.Any(part =>
                        candidateName.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0))
                    return Invalid(result, "Package is not the NXE-approved Series build/channel.");
                if (result.Definition.RequireAssetMarkersDuringInspection &&
                    result.Definition.RejectedAssetNameParts != null &&
                    result.Definition.RejectedAssetNameParts.Any(part =>
                        candidateName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0))
                    return Invalid(result, "Package is an explicitly unsupported build/SKU.");
                if (!result.SignaturePresent)
                    return Invalid(result, "Package signature is missing.");
                return result;
            }
            catch (InvalidDataException ex)
            {
                return Invalid(result, "Invalid APPX/MSIX container: " + ex.Message);
            }
            catch (Exception ex)
            {
                return Invalid(result, "Package inspection failed: " + ex.Message);
            }
        }

        private static bool IsBundleExtension(string path)
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(".appxbundle", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".msixbundle", StringComparison.OrdinalIgnoreCase);
        }

        private static void InspectBundle(ZipArchive archive, EmulatorPackageInspection result)
        {
            result.IsBundle = true;
            result.SignaturePresent = FindEntry(archive, "AppxSignature.p7x") != null;
            var manifestEntry = FindEntry(archive, "AppxMetadata/AppxBundleManifest.xml");
            if (manifestEntry == null)
            {
                Invalid(result, "Bundle manifest was not found.");
                return;
            }

            XDocument bundleManifest;
            using (var input = manifestEntry.Open())
                bundleManifest = XDocument.Load(input);

            var x64Package = bundleManifest.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "Package" &&
                string.Equals(Attribute(element, "Architecture"), "x64", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Attribute(element, "Type"), "resource", StringComparison.OrdinalIgnoreCase));
            if (x64Package == null)
            {
                Invalid(result, "Bundle does not contain an x64 application package.");
                return;
            }

            var fileName = Attribute(x64Package, "FileName");
            var nestedEntry = archive.Entries.FirstOrDefault(entry =>
                string.Equals(entry.FullName.Replace('\\', '/'), fileName.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (nestedEntry == null)
            {
                Invalid(result, "Bundle x64 application payload was not found.");
                return;
            }

            using (var nestedStream = nestedEntry.Open())
            using (var buffered = new MemoryStream())
            {
                nestedStream.CopyTo(buffered);
                buffered.Position = 0;
                using (var nestedArchive = new ZipArchive(buffered, ZipArchiveMode.Read, false))
                {
                    var outerSignature = result.SignaturePresent;
                    InspectApplicationPackage(nestedArchive, result);
                    result.SignaturePresent = outerSignature || result.SignaturePresent;
                }
            }
        }

        private static void InspectApplicationPackage(ZipArchive archive, EmulatorPackageInspection result)
        {
            result.SignaturePresent = result.SignaturePresent || FindEntry(archive, "AppxSignature.p7x") != null;
            var manifestEntry = FindEntry(archive, "AppxManifest.xml");
            if (manifestEntry == null)
            {
                Invalid(result, "AppxManifest.xml was not found.");
                return;
            }

            XDocument manifest;
            using (var input = manifestEntry.Open())
                manifest = XDocument.Load(input);
            var identity = manifest.Descendants().FirstOrDefault(element => element.Name.LocalName == "Identity");
            if (identity == null)
            {
                Invalid(result, "Package Identity was not found.");
                return;
            }

            result.IdentityName = Attribute(identity, "Name");
            result.Publisher = Attribute(identity, "Publisher");
            result.Architecture = Attribute(identity, "ProcessorArchitecture");
            Version version;
            if (!Version.TryParse(Attribute(identity, "Version"), out version))
            {
                Invalid(result, "Package version is invalid.");
                return;
            }
            result.Version = version;
            result.Protocols = manifest.Descendants()
                .Where(element => element.Name.LocalName == "Protocol")
                .Select(element => Attribute(element, "Name"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static ZipArchiveEntry FindEntry(ZipArchive archive, string name)
        {
            var normalized = name.Replace('\\', '/');
            return archive.Entries.FirstOrDefault(entry =>
                string.Equals(entry.FullName.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static string Attribute(XElement element, string name)
        {
            var attribute = element.Attributes().FirstOrDefault(item => item.Name.LocalName == name);
            return attribute == null ? string.Empty : attribute.Value;
        }

        private static EmulatorPackageInspection Invalid(EmulatorPackageInspection result, string error)
        {
            result.Error = error;
            return result;
        }
    }
}
