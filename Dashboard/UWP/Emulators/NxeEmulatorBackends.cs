using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NxeDashboard.Library;

namespace NxeDashboard.Emulators
{
    public interface IBackendLaunchAdapter
    {
        Task<bool> IsAvailableAsync(string backendId);
        Task LaunchAsync(string backendId, NxeGameRecord game);
        bool CanSaveAndQuit(string backendId);
        Task SaveAndQuitAsync(string backendId);
    }

    public interface IEmulatorBackend
    {
        string BackendId { get; }
        string DisplayName { get; }
        IReadOnlyCollection<string> SupportedPlatforms { get; }
        IReadOnlyCollection<string> SupportedExtensions { get; }
        Task<bool> IsAvailableAsync();
        Task<bool> ValidateAsync(NxeGameRecord game);
        Task LaunchAsync(NxeGameRecord game);
        NxeCompatibilityStatus NormalizeCompatibility(string backendStatus);
    }

    public sealed class EmulatorBackendRegistry
    {
        private readonly Dictionary<string, IEmulatorBackend> backends;
        public EmulatorBackendRegistry(IBackendLaunchAdapter launchAdapter)
        {
            var all = new IEmulatorBackend[]
            {
                new XeniaBackend(launchAdapter),
                new Xbsx2Backend(launchAdapter),
                new DolphinBackend(launchAdapter),
                new FlycastBackend(launchAdapter),
                new RetroArchBackend(launchAdapter)
            };
            backends = all.ToDictionary(backend => backend.BackendId, StringComparer.OrdinalIgnoreCase);
        }
        public IReadOnlyCollection<IEmulatorBackend> All => backends.Values;
        public IEmulatorBackend For(NxeGameRecord game) =>
            game != null && backends.TryGetValue(game.BackendId ?? string.Empty, out var backend) ? backend : null;
    }

    public abstract class EmulatorBackendBase : IEmulatorBackend
    {
        private readonly IBackendLaunchAdapter launchAdapter;
        protected EmulatorBackendBase(IBackendLaunchAdapter adapter, string id, string name,
            string[] platforms, string[] extensions)
        {
            launchAdapter = adapter;
            BackendId = id;
            DisplayName = name;
            SupportedPlatforms = platforms;
            SupportedExtensions = extensions;
        }

        public string BackendId { get; }
        public string DisplayName { get; }
        public IReadOnlyCollection<string> SupportedPlatforms { get; }
        public IReadOnlyCollection<string> SupportedExtensions { get; }

        public Task<bool> IsAvailableAsync() => launchAdapter.IsAvailableAsync(BackendId);

        public Task<bool> ValidateAsync(NxeGameRecord game) =>
            Task.FromResult(game != null && game.IsAvailable &&
                string.Equals(game.BackendId, BackendId, StringComparison.OrdinalIgnoreCase));

        public async Task LaunchAsync(NxeGameRecord game)
        {
            if (!await ValidateAsync(game))
                throw new InvalidOperationException("The selected game is not available for " + DisplayName + ".");
            if (!await IsAvailableAsync())
                throw new InvalidOperationException(DisplayName + " is not installed on this Xbox console.");
            await launchAdapter.LaunchAsync(BackendId, game);
        }

        public virtual NxeCompatibilityStatus NormalizeCompatibility(string status)
        {
            switch ((status ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "perfect": return NxeCompatibilityStatus.Perfect;
                case "playable": return NxeCompatibilityStatus.Playable;
                case "ingame": case "in-game": return NxeCompatibilityStatus.InGame;
                case "intro": return NxeCompatibilityStatus.Intro;
                case "loads": case "menu": return NxeCompatibilityStatus.Loads;
                case "broken": case "nothing": return NxeCompatibilityStatus.Broken;
                default: return NxeCompatibilityStatus.Unknown;
            }
        }
    }

    public sealed class XeniaBackend : EmulatorBackendBase
    {
        public XeniaBackend(IBackendLaunchAdapter a)
            : base(a, "xenia", "Xenia Canary", new[] { "Xbox360", "Xbox 360", "Xenia" },
                   new[] { ".iso", ".xex", ".live", ".con", ".pirs", "", "." }) { }
    }

    public sealed class Xbsx2Backend : EmulatorBackendBase
    {
        public Xbsx2Backend(IBackendLaunchAdapter a)
            : base(a, "xbsx2", "XBSX2", new[] { "PS2", "PlayStation 2", "XBSX2" },
                   new[] { ".iso", ".bin", ".chd", ".cso" }) { }
    }

    public sealed class DolphinBackend : EmulatorBackendBase
    {
        public DolphinBackend(IBackendLaunchAdapter a)
            : base(a, "dolphin", "Dolphin", new[] { "GameCube", "Wii", "Dolphin" },
                   new[] { ".iso", ".gcm", ".wbfs", ".rvz", ".wad" }) { }
    }

    public sealed class FlycastBackend : EmulatorBackendBase
    {
        public FlycastBackend(IBackendLaunchAdapter a)
            : base(a, "flycast", "Flycast", new[] { "Dreamcast", "NAOMI", "Flycast" },
                   new[] { ".chd", ".gdi", ".cdi", ".cue", ".bin" }) { }
    }

    public sealed class RetroArchBackend : EmulatorBackendBase
    {
        public RetroArchBackend(IBackendLaunchAdapter a)
            : base(a, "retroarch", "RetroArch",
                   new[]
                   {
                       "PSP", "PlayStation", "PS1", "NES", "SNES", "Nintendo 64", "N64",
                       "Game Boy", "Game Boy Color", "Game Boy Advance", "GBA", "GameBoy",
                       "Nintendo DS", "NDS", "Virtual Boy", "SG-1000", "Master System", "Game Gear",
                       "Genesis", "Mega Drive", "Sega CD", "Sega 32X", "Sega Saturn",
                       "TurboGrafx-16", "TG16", "TurboGrafx-CD", "SuperGrafx", "PC-FX",
                       "Neo Geo", "Neo Geo CD", "Neo Geo Pocket", "Neo Geo Pocket Color",
                       "WonderSwan", "WonderSwan Color", "3DO", "ColecoVision", "Intellivision",
                       "Vectrex", "Odyssey 2", "Fairchild Channel F", "Pokemon Mini", "Arcade",
                       "Atari 2600", "Atari 5200", "Atari 7800", "Atari Jaguar", "Atari Lynx", "RetroArch"
                   },
                   new[]
                   {
                       ".iso", ".cso", ".pbp", ".elf", ".prx", ".chd", ".cue", ".bin",
                       ".nes", ".sfc", ".smc", ".z64", ".n64", ".v64", ".gb", ".gbc", ".gba",
                       ".nds", ".vb", ".md", ".gen", ".sms", ".gg", ".sg", ".pce",
                       ".zip", ".7z", ".a26", ".a52", ".a78", ".j64", ".jag", ".lnx",
                       ".pcfx", ".ngp", ".ngc", ".ws", ".wsc", ".col", ".int", ".vec",
                       ".o2", ".chf", ".min"
                   }) { }
    }
}
