using System;
using System.Collections.Generic;

namespace NxeDashboard.Shared
{
    public enum EmulatorPackageSourceType
    {
        ApprovedEmbeddedArtifact,
        GitHubLatestRelease
    }

    public sealed class EmulatorPackageDefinition
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Systems { get; set; }
        public IReadOnlyList<string> Platforms { get; set; }
        public string SupportedConsoleGeneration { get; set; }
        public IReadOnlyList<string> AcceptedPackageIdentities { get; set; }
        public IReadOnlyList<string> AcceptedPublishers { get; set; }
        public string ExpectedProtocol { get; set; }
        public string Architecture { get; set; }
        public string CanonicalDestinationFileName { get; set; }
        public EmulatorPackageSourceType SourceType { get; set; }
        public string GitHubRepository { get; set; }
        public IReadOnlyList<string> RequiredAssetNameParts { get; set; }
        public IReadOnlyList<string> RejectedAssetNameParts { get; set; }
        public bool RequireAssetMarkersDuringInspection { get; set; }
        public string EmbeddedResourceName { get; set; }
        public Version MinimumVersion { get; set; }
    }

    /// <summary>
    /// One source of truth for the emulator launch contract and Xbox/UWP package selection.
    /// Package files are staged in the already-established NXE Transfers root.
    /// </summary>
    public static class EmulatorPackageManifest
    {
        public const string PackageRelativeDirectory = "Transfers";

        public static readonly EmulatorPackageDefinition[] Definitions =
        {
            new EmulatorPackageDefinition
            {
                Id = "xenia",
                DisplayName = "Xenia Canary UWP",
                Systems = "Xbox 360",
                Platforms = new[] { "Xbox360", "Xbox 360", "Xenia" },
                SupportedConsoleGeneration = "Xbox Series X|S",
                AcceptedPackageIdentities = new[] { "27d6ba69-e4f1-4b69-b391-8238bab5a431" },
                AcceptedPublishers = new[] { "CN=SirMangler" },
                ExpectedProtocol = "xeniacanary",
                Architecture = "x64",
                CanonicalDestinationFileName = "Xenia Canary UWP.appx",
                SourceType = EmulatorPackageSourceType.ApprovedEmbeddedArtifact,
                EmbeddedResourceName = "NXE.EmulatorPackages.XeniaCanary.appx",
                MinimumVersion = new Version(1, 1, 5, 2)
            },
            new EmulatorPackageDefinition
            {
                Id = "xbsx2",
                DisplayName = "XBSX2",
                Systems = "PlayStation 2",
                Platforms = new[] { "PS2", "PlayStation 2", "XBSX2" },
                SupportedConsoleGeneration = "Xbox Series X|S",
                AcceptedPackageIdentities = new[] { "595c25f0-b370-4d7a-9b14-3027fbd69ecf" },
                AcceptedPublishers = new[] { "CN=SternXD" },
                ExpectedProtocol = "xbsx2",
                Architecture = "x64",
                CanonicalDestinationFileName = "XBSX2 AVX2.msixbundle",
                SourceType = EmulatorPackageSourceType.GitHubLatestRelease,
                GitHubRepository = "XboxEmulationHub/XBSX2",
                RequiredAssetNameParts = new[] { "AVX2" },
                RejectedAssetNameParts = new[] { "SSE4" },
                RequireAssetMarkersDuringInspection = true
            },
            new EmulatorPackageDefinition
            {
                Id = "dolphin",
                DisplayName = "Dolphin UWP",
                Systems = "GameCube / Wii",
                Platforms = new[] { "GameCube", "Wii", "Dolphin" },
                SupportedConsoleGeneration = "Xbox Series X|S",
                AcceptedPackageIdentities = new[] { "3143e227-cbe5-41c4-aaa9-cf40132a1b22" },
                AcceptedPublishers = new[] { "CN=Stern" },
                ExpectedProtocol = "dolphin",
                Architecture = "x64",
                CanonicalDestinationFileName = "DolphinWinRT UWP x64.msix",
                SourceType = EmulatorPackageSourceType.GitHubLatestRelease,
                GitHubRepository = "SternXD/dolphin",
                RequiredAssetNameParts = new[] { "DolphinWinRT", "x64" },
                RejectedAssetNameParts = new[] { "symbols", "source" }
            },
            new EmulatorPackageDefinition
            {
                Id = "flycast",
                DisplayName = "Flycast UWP",
                Systems = "Dreamcast / NAOMI",
                Platforms = new[] { "Dreamcast", "NAOMI", "Flycast" },
                SupportedConsoleGeneration = "Xbox Series X|S",
                AcceptedPackageIdentities = new[] { "AF75D068-D5AC-3D3C-B52A-2791C2F3491A" },
                AcceptedPublishers = new[] { "CN=Flyinghead" },
                ExpectedProtocol = "flycast",
                Architecture = "x64",
                CanonicalDestinationFileName = "Flycast UWP.appx",
                SourceType = EmulatorPackageSourceType.GitHubLatestRelease,
                GitHubRepository = "flyinghead/flycast",
                RequiredAssetNameParts = new[] { "flycast" },
                RejectedAssetNameParts = new[] { "android", "win64", "linux", "mac" }
            },
            new EmulatorPackageDefinition
            {
                Id = "retroarch",
                DisplayName = "RetroArch UWP",
                Systems = "Classic systems / Arcade / PSP",
                Platforms = new[] { "RetroArch", "PSP", "PlayStation", "PS1" },
                SupportedConsoleGeneration = "Xbox Series X|S",
                AcceptedPackageIdentities = new[] { "1e4cf179-f3c2-404f-b9f3-cb2070a5aad8" },
                AcceptedPublishers = new[] { "CN=libretro" },
                ExpectedProtocol = "retroarch",
                Architecture = "x64",
                CanonicalDestinationFileName = "RetroArch-SeriesConsoles-AllCores.appx",
                SourceType = EmulatorPackageSourceType.GitHubLatestRelease,
                GitHubRepository = "XboxEmulationHub/RetroArch",
                // NXE deliberately pins the Series All Cores channel because PSP uses its PPSSPP core.
                RequiredAssetNameParts = new[] { "SeriesConsoles", "AllCores" },
                RejectedAssetNameParts = new[] { "XboxOne" },
                RequireAssetMarkersDuringInspection = true
            }
        };

        public static EmulatorPackageDefinition Find(string id)
        {
            foreach (var definition in Definitions)
            {
                if (string.Equals(definition.Id, id, StringComparison.OrdinalIgnoreCase))
                    return definition;
            }
            return null;
        }
    }
}
