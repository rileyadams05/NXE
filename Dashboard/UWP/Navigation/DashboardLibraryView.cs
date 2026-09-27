using NxeDashboard.Library;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NxeDashboard.Navigation
{
    public static class DashboardLibraryView
    {
        public const string All = "All Games";
        public const string Favorites = "Favorites";
        public const string Recent = "Recently Played";
        public const string Retro = "Retro Systems";

        private static readonly HashSet<string> RetroPlatforms = new HashSet<string>(
            new[] { "NES", "SNES", "N64", "GameBoy", "GBA", "Genesis", "Arcade" },
            StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<NxeGameRecord> Apply(
            IEnumerable<NxeGameRecord> source, string filter)
        {
            var records = source ?? Enumerable.Empty<NxeGameRecord>();
            if (string.Equals(filter, Favorites, StringComparison.OrdinalIgnoreCase))
                return records.Where(game => game.IsFavorite).ToList();
            if (string.Equals(filter, Recent, StringComparison.OrdinalIgnoreCase))
                return records.Where(game => game.LastPlayed.HasValue)
                    .OrderByDescending(game => game.LastPlayed).ToList();
            if (string.Equals(filter, Retro, StringComparison.OrdinalIgnoreCase))
                return records.Where(game => RetroPlatforms.Contains(game.Platform)).ToList();
            if (!string.IsNullOrWhiteSpace(filter) &&
                !string.Equals(filter, All, StringComparison.OrdinalIgnoreCase))
                return records.Where(game => string.Equals(
                    game.Platform, filter, StringComparison.OrdinalIgnoreCase)).ToList();
            return records.ToList();
        }
    }
}
