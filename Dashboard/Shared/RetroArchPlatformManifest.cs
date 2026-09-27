using System;
using System.Collections.Generic;
using System.Linq;

namespace NxeDashboard.Shared
{
    public sealed class RetroArchPlatform
    {
        public string Id { get; set; }
        public string FolderName { get; set; }
        public string DisplayName { get; set; }
        public IReadOnlyList<string> CoreCandidates { get; set; }
        public IReadOnlyList<string> Extensions { get; set; }
        public IReadOnlyList<string> BiosRequirements { get; set; }
    }

    public static class RetroArchPlatformManifest
    {
        public static readonly IReadOnlyList<RetroArchPlatform> Platforms = new[]
        {
            P("atari2600", "Atari 2600", new[] { "stella_libretro.dll" }, ".a26", ".bin"),
            P("atari5200", "Atari 5200", new[] { "a5200_libretro.dll" }, ".a52", ".bin"),
            P("atari7800", "Atari 7800", new[] { "prosystem_libretro.dll" }, ".a78", ".bin"),
            P("atarijaguar", "Atari Jaguar", new[] { "virtualjaguar_libretro.dll" }, ".j64", ".jag"),
            P("atarilynx", "Atari Lynx", new[] { "handy_libretro.dll" }, ".lnx"),
            P("nes", "NES", new[] { "mesen_libretro.dll", "fceumm_libretro.dll" }, ".nes"),
            P("snes", "SNES", new[] { "snes9x_libretro.dll" }, ".sfc", ".smc"),
            P("n64", "Nintendo 64", new[] { "mupen64plus_next_libretro.dll", "parallel_n64_libretro.dll" }, ".z64", ".n64", ".v64"),
            P("gb", "Game Boy", new[] { "gambatte_libretro.dll", "mgba_libretro.dll" }, ".gb"),
            P("gbc", "Game Boy Color", new[] { "gambatte_libretro.dll", "mgba_libretro.dll" }, ".gbc"),
            P("gba", "Game Boy Advance", new[] { "mgba_libretro.dll" }, ".gba"),
            P("nds", "Nintendo DS", new[] { "melonds_libretro.dll", "desmume_libretro.dll" }, ".nds"),
            P("virtualboy", "Virtual Boy", new[] { "mednafen_vb_libretro.dll" }, ".vb"),
            P("sg1000", "SG-1000", new[] { "genesis_plus_gx_libretro.dll" }, ".sg"),
            P("mastersystem", "Master System", new[] { "genesis_plus_gx_libretro.dll" }, ".sms"),
            P("gamegear", "Game Gear", new[] { "genesis_plus_gx_libretro.dll" }, ".gg"),
            P("genesis", "Genesis", new[] { "genesis_plus_gx_libretro.dll", "picodrive_libretro.dll" }, ".md", ".gen", ".bin"),
            P("segacd", "Sega CD", new[] { "genesis_plus_gx_libretro.dll", "picodrive_libretro.dll" }, ".cue", ".chd"),
            P("sega32x", "Sega 32X", new[] { "picodrive_libretro.dll" }, ".32x", ".bin"),
            P("saturn", "Sega Saturn", new[] { "mednafen_saturn_libretro.dll", "yabause_libretro.dll" }, ".cue", ".chd"),
            P("playstation", "PlayStation", new[] { "beetle_psx_hw_libretro.dll", "pcsx_rearmed_libretro.dll" }, ".cue", ".chd", ".pbp", ".bin"),
            P("psp", "PSP", new[] { "ppsspp_libretro.dll" }, new[] { "PPSSPP assets in RetroArch System directory" }, ".iso", ".cso", ".pbp", ".chd", ".elf"),
            P("tg16", "TurboGrafx-16", new[] { "mednafen_pce_fast_libretro.dll" }, ".pce"),
            P("tgcd", "TurboGrafx-CD", new[] { "mednafen_pce_fast_libretro.dll" }, ".cue", ".chd"),
            P("supergrafx", "SuperGrafx", new[] { "mednafen_supergrafx_libretro.dll" }, ".pce"),
            P("pcfx", "PC-FX", new[] { "mednafen_pcfx_libretro.dll" }, ".cue", ".chd", ".pcfx"),
            P("neogeo", "Neo Geo", new[] { "fbneo_libretro.dll" }, ".zip", ".7z"),
            P("neogeocd", "Neo Geo CD", new[] { "fbneo_libretro.dll" }, ".cue", ".chd"),
            P("ngp", "Neo Geo Pocket", new[] { "mednafen_ngp_libretro.dll" }, ".ngp"),
            P("ngpc", "Neo Geo Pocket Color", new[] { "mednafen_ngp_libretro.dll" }, ".ngc"),
            P("wonderswan", "WonderSwan", new[] { "mednafen_wswan_libretro.dll" }, ".ws"),
            P("wonderswancolor", "WonderSwan Color", new[] { "mednafen_wswan_libretro.dll" }, ".wsc"),
            P("3do", "3DO", new[] { "opera_libretro.dll" }, ".cue", ".chd", ".iso"),
            P("colecovision", "ColecoVision", new[] { "gearcoleco_libretro.dll" }, ".col", ".rom"),
            P("intellivision", "Intellivision", new[] { "freeintv_libretro.dll" }, ".int", ".bin"),
            P("vectrex", "Vectrex", new[] { "vecx_libretro.dll" }, ".vec", ".bin"),
            P("odyssey2", "Odyssey 2", new[] { "o2em_libretro.dll" }, ".o2", ".bin"),
            P("channelf", "Fairchild Channel F", new[] { "freechaf_libretro.dll" }, ".chf", ".bin"),
            P("pokemonmini", "Pokemon Mini", new[] { "pokemini_libretro.dll" }, ".min"),
            P("arcade", "Arcade", new[] { "fbneo_libretro.dll", "mame2003_plus_libretro.dll" }, ".zip", ".7z")
        };

        public static readonly string[] FolderNames = Platforms.Select(platform => platform.FolderName).ToArray();

        public static RetroArchPlatform Find(string idOrFolder) => Platforms.FirstOrDefault(platform =>
            string.Equals(platform.Id, idOrFolder, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(platform.FolderName, idOrFolder, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(platform.DisplayName, idOrFolder, StringComparison.OrdinalIgnoreCase));

        private static RetroArchPlatform P(string id, string name, string[] cores, params string[] extensions) =>
            P(id, name, cores, new string[0], extensions);

        private static RetroArchPlatform P(string id, string name, string[] cores, string[] bios, params string[] extensions) =>
            new RetroArchPlatform
            {
                Id = id,
                FolderName = name,
                DisplayName = name,
                CoreCandidates = cores.Select(core => @"cores\" + core).ToArray(),
                Extensions = extensions,
                BiosRequirements = bios
            };
    }
}
