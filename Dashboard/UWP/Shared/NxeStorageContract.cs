using System;
using System.Collections.Generic;

namespace NxeDashboard.Shared
{
    public static class NxeStorageContract
    {
        public static readonly string[] TopLevel =
        {
            "Games", "EmulatorData", "BIOS", "Covers", "Metadata",
            "Saves", "Config", "Cache", "Transfers", "NXE Themes"
        };

        public static readonly string[] DedicatedGameFolders =
        {
            "Xenia", "Dolphin", "XBSX2", "Flycast"
        };

        public static readonly string[] GameFolders =
        {
            "Xenia", "Dolphin", "XBSX2", "Flycast", "RetroArch",
            "Xbox360", "PS2", "GameCube", "Wii", "Dreamcast"
        };

        public static readonly string[] RetroArchSystemFolders = RetroArchPlatformManifest.FolderNames;

        public static readonly string[] EmulatorFolders =
        {
            "Xenia", "XBSX2", "Dolphin", "Flycast", "RetroArch"
        };

        public static readonly string[] BiosFolders =
        {
            "Dolphin", "Flycast", "RetroArch", "XBSX2"
        };

        private static readonly Dictionary<string, string> Backends =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Xenia"] = "xenia", ["Xbox360"] = "xenia", ["Xbox 360"] = "xenia",
                ["XBSX2"] = "xbsx2", ["PS2"] = "xbsx2", ["PlayStation 2"] = "xbsx2",
                ["Dolphin"] = "dolphin", ["GameCube"] = "dolphin", ["Wii"] = "dolphin",
                ["Flycast"] = "flycast", ["Dreamcast"] = "flycast", ["NAOMI"] = "flycast",
                ["RetroArch"] = "retroarch",
                ["PSP"] = "retroarch",
                ["PlayStation"] = "retroarch", ["PS1"] = "retroarch",
                ["NES"] = "retroarch",
                ["SNES"] = "retroarch",
                ["Nintendo 64"] = "retroarch", ["N64"] = "retroarch",
                ["Game Boy"] = "retroarch", ["GameBoy"] = "retroarch",
                ["Game Boy Color"] = "retroarch", ["GBC"] = "retroarch",
                ["Game Boy Advance"] = "retroarch", ["GBA"] = "retroarch",
                ["Nintendo DS"] = "retroarch", ["NDS"] = "retroarch",
                ["Virtual Boy"] = "retroarch",
                ["SG-1000"] = "retroarch",
                ["Master System"] = "retroarch",
                ["Game Gear"] = "retroarch",
                ["Genesis"] = "retroarch", ["Mega Drive"] = "retroarch",
                ["Sega CD"] = "retroarch",
                ["Sega 32X"] = "retroarch",
                ["Sega Saturn"] = "retroarch",
                ["TurboGrafx-16"] = "retroarch", ["TG16"] = "retroarch",
                ["TurboGrafx-CD"] = "retroarch",
                ["SuperGrafx"] = "retroarch",
                ["PC-FX"] = "retroarch",
                ["Neo Geo"] = "retroarch",
                ["Neo Geo CD"] = "retroarch",
                ["Neo Geo Pocket"] = "retroarch",
                ["Neo Geo Pocket Color"] = "retroarch",
                ["WonderSwan"] = "retroarch",
                ["WonderSwan Color"] = "retroarch",
                ["3DO"] = "retroarch",
                ["ColecoVision"] = "retroarch",
                ["Intellivision"] = "retroarch",
                ["Vectrex"] = "retroarch",
                ["Odyssey 2"] = "retroarch",
                ["Fairchild Channel F"] = "retroarch",
                ["Pokemon Mini"] = "retroarch",
                ["Arcade"] = "retroarch",
                ["Atari 2600"] = "retroarch",
                ["Atari 5200"] = "retroarch",
                ["Atari 7800"] = "retroarch",
                ["Atari Jaguar"] = "retroarch",
                ["Atari Lynx"] = "retroarch"
            };

        public static string BackendFor(string platformOrEmulator) =>
            Backends.TryGetValue(platformOrEmulator ?? string.Empty, out var backend)
                ? backend
                : (platformOrEmulator ?? "unknown").ToLowerInvariant();

        public static string PlatformFor(string folderOrPlatform, string extension)
        {
            var clean = (folderOrPlatform ?? string.Empty).Trim();
            if (string.Equals(clean, "Xenia", StringComparison.OrdinalIgnoreCase)) return "Xbox360";
            if (string.Equals(clean, "XBSX2", StringComparison.OrdinalIgnoreCase)) return "PS2";
            if (string.Equals(clean, "Flycast", StringComparison.OrdinalIgnoreCase)) return "Dreamcast";
            if (string.Equals(clean, "Dolphin", StringComparison.OrdinalIgnoreCase))
            {
                var ext = (extension ?? string.Empty).ToLowerInvariant();
                if (ext == ".wbfs" || ext == ".wad") return "Wii";
                return "GameCube";
            }
            if (string.Equals(clean, "RetroArch", StringComparison.OrdinalIgnoreCase))
            {
                var ext = (extension ?? string.Empty).ToLowerInvariant();
                if (ext == ".iso" || ext == ".cso" || ext == ".elf" || ext == ".prx") return "PSP";
                if (ext == ".sfc" || ext == ".smc") return "SNES";
                if (ext == ".nes") return "NES";
                if (ext == ".z64" || ext == ".n64" || ext == ".v64") return "Nintendo 64";
                if (ext == ".gba") return "Game Boy Advance";
                if (ext == ".gb" || ext == ".gbc") return "Game Boy";
                if (ext == ".nds") return "Nintendo DS";
                if (ext == ".md" || ext == ".gen") return "Genesis";
                if (ext == ".cue" || ext == ".chd" || ext == ".pbp") return "PlayStation";
                if (ext == ".zip" || ext == ".7z") return "Arcade";
                return "NES";
            }
            return clean;
        }

        public static string NormalizeVirtualPath(string currentDirectory, string requested)
        {
            requested = (requested ?? string.Empty).Replace('\\', '/').Trim();
            var current = string.IsNullOrWhiteSpace(currentDirectory) ? "/" : currentDirectory;
            var combined = requested.StartsWith("/", StringComparison.Ordinal)
                ? requested : current.TrimEnd('/') + "/" + requested;
            var parts = new List<string>();
            foreach (var part in combined.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                if (part == "..")
                {
                    if (parts.Count == 0) throw new ArgumentException("Path escapes the FTP root.");
                    parts.RemoveAt(parts.Count - 1);
                    continue;
                }
                if (part.IndexOfAny(new[] { '\0', '\r', '\n', ':' }) >= 0)
                    throw new ArgumentException("Invalid virtual path.");
                parts.Add(part);
            }
            return "/" + string.Join("/", parts);
        }
    }
}
