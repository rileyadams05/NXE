using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NxeDashboard.Library;
using NxeDashboard.Runtime;
using NxeDashboard.Shared;

namespace NxeDashboard.Emulators
{
    public enum EmulatorDetectionState
    {
        Checking,
        Installed,
        InstalledUnavailable,
        NotInstalled,
        ProtocolUnsupported,
        DetectionError
    }

    public enum EmulatorLaunchState
    {
        Idle,
        Launching,
        ActivationRequested,
        Returned,
        LaunchFailed
    }

    public sealed class EmulatorDefinition
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Systems { get; set; }
        public string Protocol { get; set; }
        public IReadOnlyList<string> Platforms { get; set; }
    }

    public sealed class EmulatorDetectionResult
    {
        public EmulatorDefinition Definition { get; set; }
        public EmulatorDetectionState State { get; set; }
        public int? HResult { get; set; }

        public string DisplayStatus
        {
            get
            {
                switch (State)
                {
                    case EmulatorDetectionState.Checking: return "Checking";
                    case EmulatorDetectionState.Installed: return "Installed";
                    case EmulatorDetectionState.InstalledUnavailable: return "Installed / unavailable";
                    case EmulatorDetectionState.NotInstalled: return "Not Installed";
                    case EmulatorDetectionState.ProtocolUnsupported: return "Protocol Unsupported";
                    default: return "Detection Error";
                }
            }
        }
    }

    public sealed class EmulatorManager
    {
        private static readonly EmulatorDefinition[] DefinitionsValue =
            EmulatorPackageManifest.Definitions.Select(item => new EmulatorDefinition
            {
                Id = item.Id,
                DisplayName = item.DisplayName,
                Systems = item.Systems,
                Protocol = item.ExpectedProtocol,
                Platforms = item.Platforms
            }).ToArray();

        public IReadOnlyList<EmulatorDefinition> Definitions => DefinitionsValue;
        public EmulatorLaunchState LaunchState { get; private set; } = EmulatorLaunchState.Idle;
        public string ActiveBackend { get; private set; }
        public string ActiveGameId { get; private set; }
        public string LastError { get; private set; }

        public EmulatorDefinition Find(string id) =>
            DefinitionsValue.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

        public async Task<EmulatorDetectionResult> DetectAsync(string id)
        {
            var definition = Find(id);
            if (definition == null)
            {
                return new EmulatorDetectionResult { State = EmulatorDetectionState.DetectionError };
            }

            try
            {
                var available = await new EmbeddedEmulatorHost().IsAvailableAsync(id);
                var state = available ? EmulatorDetectionState.Installed : EmulatorDetectionState.NotInstalled;
                await LogDetectionAsync(definition, "embedded=" + available, state, null);
                return new EmulatorDetectionResult { Definition = definition, State = state };
            }
            catch (Exception exception)
            {
                await LogDetectionAsync(definition, "exception", EmulatorDetectionState.DetectionError, exception.HResult);
                return new EmulatorDetectionResult
                {
                    Definition = definition,
                    State = EmulatorDetectionState.DetectionError,
                    HResult = exception.HResult
                };
            }
        }

        public IReadOnlyList<string> ResolveRetroArchCoreCandidates(string platform)
        {
            var definition = RetroArchPlatformManifest.Find((platform ?? string.Empty).Trim());
            return definition == null ? new string[0] : definition.CoreCandidates;
        }

        public void MarkLaunching(NxeGameRecord game)
        {
            LaunchState = EmulatorLaunchState.Launching;
            ActiveBackend = game?.BackendId;
            ActiveGameId = game?.InternalId;
            LastError = null;
        }

        public void MarkActivationRequested() => LaunchState = EmulatorLaunchState.ActivationRequested;
        public void MarkReturned() => LaunchState = EmulatorLaunchState.Returned;
        public void MarkFailed(Exception exception)
        {
            LaunchState = EmulatorLaunchState.LaunchFailed;
            LastError = exception?.Message;
        }

        private static Task LogDetectionAsync(EmulatorDefinition definition, string query,
            EmulatorDetectionState state, int? hresult)
        {
            var message = "[EMU-DETECT] emulator=" + definition.Id +
                " embedded-host=" + query +
                " status=" + state + (hresult.HasValue ? " HRESULT=0x" + hresult.Value.ToString("X8") : string.Empty);
            return ExternalStorageManager.LogDiagnosticAsync(message);
        }
    }
}
