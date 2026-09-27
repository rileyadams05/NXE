using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NxeDashboard.Library
{
    public interface INxeGameImporter
    {
        string ImporterId { get; }
        IReadOnlyCollection<string> Platforms { get; }
        bool Supports(NxeFileCandidate candidate);
        Task<NxeGameRecord> ImportAsync(NxeFileCandidate candidate);
    }

    public sealed class NxeImporterRegistry
    {
        private readonly IReadOnlyList<INxeGameImporter> importers;
        public NxeImporterRegistry()
        {
            importers = new INxeGameImporter[]
            {
                new XeniaImporter(),
                new Xbsx2Importer(),
                new DolphinImporter(),
                new FlycastImporter(),
                new RetroArchImporter()
            };
        }

        public IReadOnlyList<INxeGameImporter> All => importers;
        public INxeGameImporter For(NxeFileCandidate candidate) =>
            importers.FirstOrDefault(importer => importer.Supports(candidate));
    }

    public abstract class ImporterBase : INxeGameImporter
    {
        private readonly HashSet<string> platforms;
        private readonly HashSet<string> extensions;
        protected ImporterBase(string id, IEnumerable<string> platforms, IEnumerable<string> extensions)
        {
            ImporterId = id;
            this.platforms = new HashSet<string>(platforms, StringComparer.OrdinalIgnoreCase);
            this.extensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        }

        public string ImporterId { get; }
        public IReadOnlyCollection<string> Platforms => platforms;
        protected abstract string BackendId { get; }
        public virtual bool Supports(NxeFileCandidate candidate)
        {
            if (candidate == null) return false;
            if (candidate.FileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return false;
            if (!platforms.Contains(candidate.Platform)) return false;
            var ext = candidate.Extension ?? string.Empty;
            var clean = ext.Trim('.');
            return string.IsNullOrEmpty(clean) || extensions.Contains(ext) || extensions.Contains("." + clean);
        }

        public async Task<NxeGameRecord> ImportAsync(NxeFileCandidate candidate)
        {
            if (!Supports(candidate)) return null;
            var prefix = candidate.ReadPrefixAsync == null ? new byte[0] :
                await candidate.ReadPrefixAsync(PrefixBytes(candidate));

            var isExtEmpty = string.IsNullOrWhiteSpace((candidate.Extension ?? string.Empty).Trim('.'));
            if (isExtEmpty && !IsValidExtensionlessContent(candidate, prefix))
                return null;

            var nativeId = NativeId(candidate, prefix);
            var fingerprint = nativeId ?? Fingerprint(candidate, prefix);
            var internalId = HashText(candidate.Platform.ToUpperInvariant() + "|" + fingerprint.ToUpperInvariant());
            var title = NativeTitle(candidate, prefix);
            if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(candidate.FileName);
            return new NxeGameRecord
            {
                InternalId = internalId,
                CanonicalTitle = title,
                Platform = candidate.Platform,
                BackendId = BackendId,
                NativeGameId = nativeId,
                PrimaryPath = candidate.RelativePath,
                LaunchPath = candidate.PhysicalPath,
                StorageDeviceId = candidate.StorageDeviceId,
                IsAvailable = true,
                ContainerType = ContainerType(candidate, prefix),
                Region = Region(candidate, prefix),
                CompatibilityStatus = NxeCompatibilityStatus.Unknown,
                LastSeen = DateTimeOffset.UtcNow,
                FileSize = candidate.Size,
                ModifiedUtcTicks = candidate.ModifiedUtcTicks
            };
        }

        protected virtual bool IsValidExtensionlessContent(NxeFileCandidate candidate, byte[] prefix) => false;
        protected virtual int PrefixBytes(NxeFileCandidate candidate) => 128 * 1024;
        protected virtual string NativeId(NxeFileCandidate candidate, byte[] prefix) => null;
        protected virtual string NativeTitle(NxeFileCandidate candidate, byte[] prefix) => null;
        protected virtual string ContainerType(NxeFileCandidate candidate, byte[] prefix)
        {
            var extension = (candidate.Extension ?? string.Empty).TrimStart('.').ToUpperInvariant();
            return string.IsNullOrWhiteSpace(extension) ? "UNKNOWN" : extension;
        }
        protected virtual string Region(NxeFileCandidate candidate, byte[] prefix) => RegionFromName(candidate.FileName);

        protected static string Fingerprint(NxeFileCandidate candidate, byte[] prefix)
        {
            using (var sha = SHA256.Create())
            {
                var size = Encoding.UTF8.GetBytes(candidate.Size.ToString());
                var bytes = new byte[size.Length + prefix.Length];
                Buffer.BlockCopy(size, 0, bytes, 0, size.Length);
                Buffer.BlockCopy(prefix, 0, bytes, size.Length, prefix.Length);
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
            }
        }

        protected static string HashText(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
                    .Replace("-", string.Empty).Substring(0, 32);
        }

        protected static uint BigEndianUInt32(byte[] bytes, int offset)
        {
            if (bytes == null || offset < 0 || offset + 4 > bytes.Length) return 0;
            return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
                   ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        protected static string Ascii(byte[] bytes, int offset, int count)
        {
            if (bytes == null || offset < 0 || offset + count > bytes.Length) return null;
            var value = Encoding.ASCII.GetString(bytes, offset, count).Trim('\0', ' ', '\r', '\n');
            return value.All(character => character >= 32 && character <= 126) ? value : null;
        }

        protected static string RegexId(string input, string pattern)
        {
            var match = Regex.Match(input ?? string.Empty, pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? Regex.Replace(match.Value, "[^A-Za-z0-9]", string.Empty).ToUpperInvariant() : null;
        }

        private static string RegionFromName(string name)
        {
            var value = (name ?? string.Empty).ToUpperInvariant();
            if (value.Contains("(USA") || value.Contains("[USA")) return "USA";
            if (value.Contains("(EUR") || value.Contains("[EUR")) return "Europe";
            if (value.Contains("(JPN") || value.Contains("[JPN") || value.Contains("(JAPAN")) return "Japan";
            return null;
        }
    }

    public sealed class XeniaImporter : ImporterBase
    {
        public XeniaImporter() : base("xenia-importer", new[] { "Xbox360", "Xbox 360", "Xenia" },
            new[] { ".iso", ".xex", ".live", ".con", ".pirs", "", "." }) { }
        protected override string BackendId => "xenia";
        protected override int PrefixBytes(NxeFileCandidate candidate) =>
            string.IsNullOrWhiteSpace((candidate?.Extension ?? string.Empty).Trim('.')) ? 4096 : base.PrefixBytes(candidate);
        public override bool Supports(NxeFileCandidate candidate)
        {
            if (candidate == null) return false;
            if (candidate.FileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(candidate.Platform, "Xbox360", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Platform, "Xbox 360", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Platform, "Xenia", StringComparison.OrdinalIgnoreCase) ||
                (candidate.RelativePath != null && candidate.RelativePath.IndexOf("/Xenia/", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var ext = (candidate.Extension ?? string.Empty).TrimStart('.');
                return string.IsNullOrEmpty(ext) ||
                       string.Equals(ext, "iso", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(ext, "xex", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(ext, "live", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(ext, "con", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(ext, "pirs", StringComparison.OrdinalIgnoreCase);
            }
            return base.Supports(candidate);
        }
        protected override string NativeId(NxeFileCandidate candidate, byte[] prefix)
        {
            var signature = Ascii(prefix, 0, 4);
            if ((signature == "LIVE" || signature == "PIRS" || signature == "CON") && prefix.Length >= 0x364)
            {
                var titleId = BigEndianUInt32(prefix, 0x360);
                if (titleId != 0) return titleId.ToString("X8");
            }
            if (signature == "XEX2" && prefix.Length >= 0x18)
            {
                var count = BigEndianUInt32(prefix, 0x14);
                for (var index = 0; index < count && 0x18 + index * 8 + 8 <= prefix.Length; index++)
                {
                    var entry = 0x18 + index * 8;
                    if (BigEndianUInt32(prefix, entry) != 0x00040006) continue;
                    var execution = (int)BigEndianUInt32(prefix, entry + 4);
                    var titleId = BigEndianUInt32(prefix, execution + 12);
                    if (titleId != 0) return titleId.ToString("X8");
                }
            }
            return RegexId(candidate.FileName, @"[0-9A-F]{8}");
        }
        protected override string NativeTitle(NxeFileCandidate candidate, byte[] prefix)
        {
            var signature = Ascii(prefix, 0, 4);
            if ((signature == "LIVE" || signature == "PIRS" || signature == "CON") && prefix.Length >= 0x492)
            {
                try
                {
                    var raw = Encoding.Unicode.GetString(prefix, 0x412, Math.Min(0x100, prefix.Length - 0x412));
                    var nullIndex = raw.IndexOf('\0');
                    if (nullIndex >= 0) raw = raw.Substring(0, nullIndex);
                    var clean = raw.Trim();
                    if (!string.IsNullOrWhiteSpace(clean)) return clean;
                }
                catch { }
            }
            return base.NativeTitle(candidate, prefix);
        }

        protected override string ContainerType(NxeFileCandidate candidate, byte[] prefix)
        {
            var signature = Ascii(prefix, 0, 4);
            if (signature == "CON" || signature == "PIRS" || signature == "LIVE" ||
                signature == "XEX1" || signature == "XEX2") return signature;
            return base.ContainerType(candidate, prefix);
        }

        protected override bool IsValidExtensionlessContent(NxeFileCandidate candidate, byte[] prefix)
        {
            if (prefix == null || prefix.Length < 4) return false;
            var sig = Ascii(prefix, 0, 4);
            return sig == "LIVE" || sig == "PIRS" || sig == "CON" ||
                   sig == "XEX1" || sig == "XEX2";
        }
    }

    public sealed class Xbsx2Importer : ImporterBase
    {
        public Xbsx2Importer() : base("xbsx2-importer", new[] { "PS2", "PlayStation 2", "XBSX2" },
            new[] { ".iso", ".bin", ".chd", ".cso" }) { }
        protected override string BackendId => "xbsx2";
        protected override string NativeId(NxeFileCandidate candidate, byte[] prefix) =>
            RegexId(candidate.FileName, @"(SLUS|SLES|SCES|SCUS|SLPM|SLPS|SCPS|SCAJ|SCKA)[-_ .]?\d{3}[-_ .]?\d{2}");
    }

    public sealed class DolphinImporter : ImporterBase
    {
        public DolphinImporter() : base("dolphin-importer", new[] { "GameCube", "Wii", "Dolphin" },
            new[] { ".iso", ".gcm", ".wbfs", ".rvz", ".wad" }) { }
        protected override string BackendId => "dolphin";
        protected override string NativeId(NxeFileCandidate candidate, byte[] prefix) =>
            Ascii(prefix, 0, 6) ?? RegexId(candidate.FileName, @"[A-Z0-9]{6}");
    }

    public sealed class FlycastImporter : ImporterBase
    {
        public FlycastImporter() : base("flycast-importer", new[] { "Dreamcast", "NAOMI", "Flycast" },
            new[] { ".chd", ".gdi", ".cdi", ".cue", ".bin" }) { }
        protected override string BackendId => "flycast";
        protected override string NativeId(NxeFileCandidate candidate, byte[] prefix) =>
            Ascii(prefix, 0x40, 10) ?? RegexId(candidate.FileName, @"(HDR|T|MK|T-)\d{4,}");
    }

    public sealed class RetroArchImporter : ImporterBase
    {
        public RetroArchImporter() : base("retroarch-importer",
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
        protected override string BackendId => "retroarch";
    }
}
