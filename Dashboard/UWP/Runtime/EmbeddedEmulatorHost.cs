using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using NxeDashboard.Library;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Storage;

namespace NxeDashboard.Runtime
{
    // Xenia/Flycast run through the native host. The other engines are
    // co-packaged UWP applications launched from the same AppX package.
    public sealed class EmbeddedEmulatorHost
    {
        private const string NativeLibrary = "NXE.Emulation.Native.dll";

        private static class Native
        {
            [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
            internal static extern int NXE_EmulationNative_GetBackendStatus(string backendId);

            [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
            internal static extern int NXE_EmulationNative_Start(string backendId, string gamePath);

            [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void NXE_EmulationNative_Stop();
        }

        private static readonly HashSet<string> KnownBackends =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "xenia", "xbsx2", "dolphin", "flycast", "retroarch" };

        public async Task<bool> IsAvailableAsync(string backendId)
        {
            if (!KnownBackends.Contains(backendId ?? string.Empty))
                return false;
            if (!backendId.Equals("xenia", StringComparison.OrdinalIgnoreCase) &&
                !backendId.Equals("flycast", StringComparison.OrdinalIgnoreCase))
            {
                var entries = Package.Current.GetAppListEntries();
                var id = backendId.Equals("dolphin", StringComparison.OrdinalIgnoreCase) ? "DolphinEngine" :
                         backendId.Equals("xbsx2", StringComparison.OrdinalIgnoreCase) ? "Xbsx2Engine" : "RetroArchEngine";
                return entries.Any(e => string.Equals(e.AppInfo.Id, id, StringComparison.OrdinalIgnoreCase));
            }
            try { return Native.NXE_EmulationNative_GetBackendStatus(backendId) != 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        public async Task LaunchAsync(string backendId, NxeGameRecord game)
        {
            if (!KnownBackends.Contains(backendId ?? string.Empty))
                throw new InvalidOperationException("Unknown embedded emulator backend: " + backendId);
            var gamePath = game.LaunchPath ?? game.PrimaryPath;
            if (!backendId.Equals("xenia", StringComparison.OrdinalIgnoreCase) &&
                !backendId.Equals("flycast", StringComparison.OrdinalIgnoreCase))
            {
                await WriteLaunchRecordAsync(backendId, game);
                var id = backendId.Equals("dolphin", StringComparison.OrdinalIgnoreCase) ? "DolphinEngine" :
                         backendId.Equals("xbsx2", StringComparison.OrdinalIgnoreCase) ? "Xbsx2Engine" : "RetroArchEngine";
                var entry = Package.Current.GetAppListEntries().FirstOrDefault(e => string.Equals(e.AppInfo.Id, id, StringComparison.OrdinalIgnoreCase));
                if (entry == null || !await entry.LaunchAsync())
                    throw new NotSupportedException("The embedded " + backendId + " engine is not available in this package build.");
                return;
            }
            try
            {
                if (Native.NXE_EmulationNative_Start(backendId, gamePath) == 0)
                    throw new NotSupportedException("The embedded " + backendId + " engine is not available in this package build.");
            }
            catch (DllNotFoundException ex) { throw new NotSupportedException("Native emulation host is not packaged.", ex); }
        }

        private static async Task WriteLaunchRecordAsync(string backendId, NxeGameRecord game)
        {
            var folder = ApplicationData.Current.LocalFolder;
            var core = string.Empty;
            if (string.Equals(backendId, "retroarch", StringComparison.OrdinalIgnoreCase))
            {
                core = await UwpBackendLaunchAdapter.ResolveRetroArchCoreAsync(game.Platform);
                if (string.IsNullOrWhiteSpace(core))
                    throw new NotSupportedException("No packaged RetroArch core is available for " + game.Platform + ".");
            }
            var record = string.Join("\r\n", new[]
            {
                "LaunchId=" + Guid.NewGuid().ToString("N"),
                "Engine=" + backendId,
                "System=" + (game.Platform ?? string.Empty),
                "Core=" + core,
                "CorePath=" + core,
                "GamePath=" + (game.LaunchPath ?? game.PrimaryPath ?? string.Empty),
                "CreatedUtc=" + DateTimeOffset.UtcNow.ToString("o"),
                "Timestamp=" + DateTimeOffset.UtcNow.ToString("o"),
                "PerformanceOverlay=Basic",
                "Consumed=False",
                string.Empty
            });
            var tempName = "nxe-engine-launch." + Guid.NewGuid().ToString("N") + ".tmp";
            var temp = await folder.CreateFileAsync(tempName, CreationCollisionOption.FailIfExists);
            await FileIO.WriteTextAsync(temp, record);
            await temp.RenameAsync("nxe-engine-launch.txt", NameCollisionOption.ReplaceExisting);
        }

        public Task StopAsync(string backendId)
        {
            if (!KnownBackends.Contains(backendId ?? string.Empty))
                throw new InvalidOperationException("Unknown embedded emulator backend: " + backendId);
            try { Native.NXE_EmulationNative_Stop(); }
            catch (DllNotFoundException) { }
            return Task.CompletedTask;
        }
    }
}
