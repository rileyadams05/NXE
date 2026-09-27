using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace NxeDashboard.Runtime
{
    /// <summary>
    /// Lightweight session telemetry. Child engines remain authoritative for
    /// presented-frame counters; NXE records only values actually reported by
    /// an engine and never invents CPU/GPU/FPS data.
    /// </summary>
    public sealed class PerformanceSession
    {
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        public string Backend { get; }
        public string System { get; }
        public string GamePath { get; }
        public string Core { get; }

        public PerformanceSession(string backend, string system, string gamePath, string core)
        {
            Backend = backend ?? string.Empty;
            System = system ?? string.Empty;
            GamePath = gamePath ?? string.Empty;
            Core = core ?? string.Empty;
        }

        public async Task CompleteAsync(string exitReason)
        {
            stopwatch.Stop();
            await ExternalStorageManager.LogDiagnosticAsync(
                "[NXE-PERF] system=" + System +
                " backend=" + Backend +
                " core=" + Core +
                " game=" + GamePath +
                " durationSeconds=" + stopwatch.Elapsed.TotalSeconds.ToString("F2") +
                " fps=N/A frameTimeMs=N/A onePercentLow=N/A emulationSpeed=N/A" +
                " renderer=N/A resolution=N/A audioUnderruns=N/A" +
                " exitReason=" + (exitReason ?? "unknown"));
        }
    }
}
