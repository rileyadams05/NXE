using System;

namespace NxeDashboard.Library
{
    public enum NxeCompatibilityStatus
    {
        Unknown, Perfect, Playable, InGame, Intro, Loads, Broken
    }

    public sealed class NxeGameRecord
    {
        public string InternalId { get; set; }
        public string CanonicalTitle { get; set; }
        public string Platform { get; set; }
        public string BackendId { get; set; }
        public string NativeGameId { get; set; }
        public string PrimaryPath { get; set; }
        public string LaunchPath { get; set; }
        public string StorageDeviceId { get; set; }
        public bool IsAvailable { get; set; }
        public string ContainerType { get; set; }
        public string Region { get; set; }
        public string Developer { get; set; }
        public string Publisher { get; set; }
        public DateTimeOffset? ReleaseDate { get; set; }
        public string Description { get; set; }
        public string CoverPath { get; set; }
        public string BackgroundPath { get; set; }
        public string IconPath { get; set; }
        public NxeCompatibilityStatus CompatibilityStatus { get; set; }
        public bool IsFavorite { get; set; }
        public DateTimeOffset? LastPlayed { get; set; }
        public long PlayCount { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public ulong FileSize { get; set; }
        public long ModifiedUtcTicks { get; set; }

        // UI-friendly aliases keep XAML independent of database column names.
        public string Title => CanonicalTitle;
        public string Backend => BackendId;
        public string Key => InternalId;
        public string CoverUri => CoverPath;
        public string VirtualPath => PrimaryPath;
        public ulong Size => FileSize;
    }

    public sealed class NxeStorageDevice
    {
        public string StorageDeviceId { get; set; }
        public string DisplayName { get; set; }
        public string VirtualRoot { get; set; }
        public bool IsAvailable { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    public sealed class NxeFileCandidate
    {
        public string StorageDeviceId { get; set; }
        public string Platform { get; set; }
        public string RelativePath { get; set; }
        public string PhysicalPath { get; set; }
        public string FileName { get; set; }
        public string Extension { get; set; }
        public ulong Size { get; set; }
        public long ModifiedUtcTicks { get; set; }
        public Func<int, System.Threading.Tasks.Task<byte[]>> ReadPrefixAsync { get; set; }
    }
}
