using System;
using System.Collections.Generic;

namespace NxeDashboard.Navigation
{
    public enum DashboardState
    {
        Library,
        Settings,
        GameDetails,
        Guide,
        GuideFavorites,
        FileManager,
        GuideQuitConfirmation
    }

    public sealed class DashboardNavigationController
    {
        private readonly Stack<DashboardState> history = new Stack<DashboardState>();

        public DashboardState Current { get; private set; } = DashboardState.Library;

        public event EventHandler StateChanged;

        public void NavigateTo(DashboardState destination)
        {
            if (destination == Current) return;
            history.Push(Current);
            Current = destination;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool Back()
        {
            if (Current == DashboardState.Library || history.Count == 0) return false;
            Current = history.Pop();
            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public void ResetToLibrary()
        {
            history.Clear();
            if (Current == DashboardState.Library) return;
            Current = DashboardState.Library;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
