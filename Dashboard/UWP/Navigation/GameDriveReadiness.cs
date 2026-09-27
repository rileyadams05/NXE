using System;

namespace NxeDashboard.Navigation
{
    public enum GameDriveState
    {
        NoDrive,
        DriveConnectedNoGames,
        DriveConnectedWithGames,
        DriveReadError
    }

    // Explicit state separation for external storage vs library game items.
    public static class GameDriveReadiness
    {
        public static GameDriveState ComputeState(bool driveConnected, int gameCount)
        {
            if (!driveConnected) return GameDriveState.NoDrive;
            if (gameCount <= 0) return GameDriveState.DriveConnectedNoGames;
            return GameDriveState.DriveConnectedWithGames;
        }

        public static bool ShowSetupInstructions(GameDriveState state) =>
            state == GameDriveState.NoDrive || state == GameDriveState.DriveReadError;
    }
}
