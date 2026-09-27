using System;
using System.Threading.Tasks;
using NxeDashboard.Emulators;
using NxeDashboard.Library;
using Windows.ApplicationModel;

namespace NxeDashboard.Runtime
{
    // Dashboard-side adapter for engines compiled into this package. This type
    // deliberately contains no Launcher/URI activation path.
    public sealed class UwpBackendLaunchAdapter : IBackendLaunchAdapter
    {
        private readonly EmulatorManager manager;
        private readonly EmbeddedEmulatorHost host;

        public UwpBackendLaunchAdapter(EmulatorManager manager = null,
            EmbeddedEmulatorHost host = null)
        {
            this.manager = manager ?? new EmulatorManager();
            this.host = host ?? new EmbeddedEmulatorHost();
        }

        public Task<EmulatorDetectionResult> DetectAsync(string backendId) =>
            manager.DetectAsync(backendId);

        public async Task<bool> IsAvailableAsync(string backendId)
        {
            var available = await host.IsAvailableAsync(backendId);
            await ExternalStorageManager.LogDiagnosticAsync(
                "[EMU-EMBEDDED] emulator=" + backendId + " available=" + available);
            return available;
        }

        public async Task LaunchAsync(string backendId, NxeGameRecord game)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            manager.MarkLaunching(game);
            await ExternalStorageManager.LogDiagnosticAsync(
                "[EMU-EMBEDDED] launch emulator=" + backendId +
                " game=" + game.InternalId + " path=" + game.LaunchPath);
            try
            {
                await host.LaunchAsync(backendId, game);
                manager.MarkActivationRequested();
            }
            catch (Exception exception)
            {
                manager.MarkFailed(exception);
                await ExternalStorageManager.LogDiagnosticAsync(
                    "[EMU-EMBEDDED] launch-failed emulator=" + backendId +
                    " HRESULT=0x" + exception.HResult.ToString("X8") +
                    " error=" + exception.Message);
                throw;
            }
        }

        public bool CanSaveAndQuit(string backendId) => true;

        public async Task SaveAndQuitAsync(string backendId)
        {
            await host.StopAsync(backendId);
            manager.MarkReturned();
        }

        public static string ResolveRetroArchCore(string platform)
        {
            var candidates = new EmulatorManager().ResolveRetroArchCoreCandidates(platform);
            return candidates.Count == 0 ? null : candidates[0];
        }

        public static async Task<string> ResolveRetroArchCoreAsync(string platform)
        {
            var candidates = new EmulatorManager().ResolveRetroArchCoreCandidates(platform);
            if (candidates.Count == 0) return null;
            var root = Package.Current.InstalledLocation;
            foreach (var candidate in candidates)
            {
                var relative = candidate.Replace('\\', '/');
                try
                {
                    var file = await root.GetFileAsync(relative);
                    if (file != null) return candidate;
                }
                catch { }
                try
                {
                    var file = await root.GetFileAsync("Engines/RetroArch/" + relative);
                    if (file != null) return candidate;
                }
                catch { }
            }
            return null;
        }
    }
}
