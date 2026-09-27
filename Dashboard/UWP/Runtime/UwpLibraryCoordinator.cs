using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NxeDashboard.Library;
using NxeDashboard.Navigation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace NxeDashboard.Runtime
{
    public sealed class UwpLibraryRefreshResult
    {
        public int DriveCount { get; set; }
        public bool HasReadyGameDrive { get; set; }
        public GameDriveState DriveState { get; set; }
        public string DriveIdentity { get; set; }
        public string DriveError { get; set; }
        public int ImportedCount { get; set; }
        public int GameCount { get; set; }
        public IReadOnlyList<NxeGameRecord> Games { get; set; }
    }

    public sealed class UwpLibraryCoordinator : IDisposable
    {
        private readonly ExternalStorageManager storage = new ExternalStorageManager();
        private readonly NxeLibraryService library;

        public UwpLibraryCoordinator(string databasePath)
        {
            library = new NxeLibraryService(databasePath);
        }

        public IReadOnlyList<NxeGameRecord> LoadCached(int limit = 5000) => library.LoadPage(limit, 0);

        public async Task InitializeAsync()
        {
            var cache = ApplicationData.Current.LocalCacheFolder;
            var artwork = await cache.CreateFolderAsync("Artwork", CreationCollisionOption.OpenIfExists);
            await artwork.CreateFolderAsync("Covers", CreationCollisionOption.OpenIfExists);
            await artwork.CreateFolderAsync("Backgrounds", CreationCollisionOption.OpenIfExists);
            await artwork.CreateFolderAsync("Icons", CreationCollisionOption.OpenIfExists);
        }

        public async Task<UwpLibraryRefreshResult> RefreshAsync()
        {
            library.BeginStorageDiscovery();
            var presence = await storage.ProbePresenceAsync();
            var drives = await storage.DiscoverAndPrepareAsync();
            var imported = 0;

            foreach (var drive in drives)
            {
                library.RegisterStorage(new NxeStorageDevice
                {
                    StorageDeviceId = drive.Id,
                    DisplayName = drive.DriveRoot.Name,
                    VirtualRoot = "/" + drive.VirtualName,
                    IsAvailable = true,
                    LastSeen = DateTimeOffset.UtcNow
                });
                var token = library.BeginScan(drive.Id);
                StorageFolder games = null;
                try { games = await drive.DriveRoot.GetFolderAsync("Games"); }
                catch (Exception ex) when (IsStorageException(ex)) { }

                if (games == null && drive.NxeRoot != null)
                {
                    try { games = await drive.NxeRoot.GetFolderAsync("Games"); }
                    catch (Exception ex) when (IsStorageException(ex)) { }
                }

                if (games != null)
                {
                    await ExternalStorageManager.LogDiagnosticAsync($"[STORAGE] Games exists=true readable=true drive='{drive.DriveRoot.Name}'");

                    foreach (var folderName in NxeDashboard.Shared.NxeStorageContract.GameFolders)
                    {
                        try
                        {
                            var platformFolder = await games.GetFolderAsync(folderName);
                            await ExternalStorageManager.LogDiagnosticAsync($"Found emulator/system folder: '{folderName}'");
                            imported += await ScanFolderAsync(drive, platformFolder, folderName, string.Empty, token);
                        }
                        catch (Exception ex) when (IsStorageException(ex)) { }
                    }
                }
                else
                {
                    await ExternalStorageManager.LogDiagnosticAsync($"Drive '{drive.DriveRoot.Name}' has no readable Games folder");
                }

                library.FinishScan(drive.Id, token);
            }

            var page = library.LoadPage(5000, 0);
            var state = presence.ReadError || (presence.DriveCount > 0 && drives.Count == 0)
                ? GameDriveState.DriveReadError
                : presence.DriveCount == 0
                    ? GameDriveState.NoDrive
                    : page.Any(game => game.IsAvailable)
                        ? GameDriveState.DriveConnectedWithGames
                        : GameDriveState.DriveConnectedNoGames;
            var hasReadyGameDrive = state == GameDriveState.DriveConnectedNoGames ||
                state == GameDriveState.DriveConnectedWithGames;
            await ExternalStorageManager.LogDiagnosticAsync($"[LIBRARY] refresh Drives={presence.DriveCount}, State={state}, Ready={hasReadyGameDrive}, Imported={imported}, TotalInDb={library.Count}");

            return new UwpLibraryRefreshResult
            {
                DriveCount = presence.DriveCount,
                HasReadyGameDrive = hasReadyGameDrive,
                DriveState = state,
                DriveIdentity = presence.Identity,
                DriveError = presence.Error,
                ImportedCount = imported,
                GameCount = library.Count,
                Games = page
            };
        }

        public void SetFavorite(string gameId, bool favorite) => library.SetFavorite(gameId, favorite);
        public void RecordLaunch(string gameId) => library.RecordLaunch(gameId, DateTimeOffset.UtcNow);

        private async Task<int> ScanFolderAsync(ExternalDriveRegistration drive, StorageFolder folder,
            string folderName, string relativeDirectory, string token)
        {
            IReadOnlyList<IStorageItem> items;
            try
            {
                items = await folder.GetItemsAsync();
            }
            catch (Exception ex) when (IsStorageException(ex))
            {
                await ExternalStorageManager.LogDiagnosticAsync($"Error enumerating '{folder.Name}': {ex.Message}");
                return 0;
            }

            await ExternalStorageManager.LogDiagnosticAsync($"[STORAGE] folder='{folder.Name}' item count={items.Count}");
            if (string.Equals(folderName, "Xenia", StringComparison.OrdinalIgnoreCase))
            {
                var uno = items.OfType<StorageFile>().FirstOrDefault(file =>
                    string.Equals(file.Name, "UNO", StringComparison.OrdinalIgnoreCase));
                await ExternalStorageManager.LogDiagnosticAsync("[STORAGE] Games\\Xenia exists=true UNO visible=" + (uno != null));
                if (uno != null)
                    await ExternalStorageManager.LogDiagnosticAsync("[STORAGE] UNO StorageFile.Path='" + uno.Path + "'");
            }
            var imported = 0;

            foreach (var item in items)
            {
                if (item is StorageFolder childFolder)
                {
                    var relative = string.IsNullOrEmpty(relativeDirectory) ? childFolder.Name : relativeDirectory + "/" + childFolder.Name;
                    imported += await ScanFolderAsync(drive, childFolder, folderName, relative, token);
                }
                else if (item is StorageFile file)
                {
                    if (file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!ExternalStorageManager.IsSupportedExtension(file.FileType))
                    {
                        await ExternalStorageManager.LogDiagnosticAsync($"Skipping unsupported extension: '{file.Name}' (ext: '{file.FileType}')");
                        continue;
                    }

                    try
                    {
                        var properties = await file.GetBasicPropertiesAsync();
                        var relative = string.IsNullOrEmpty(relativeDirectory) ? file.Name : relativeDirectory + "/" + file.Name;
                        var platform = string.Equals(folderName, "RetroArch", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(relativeDirectory)
                            ? relativeDirectory.Split('/')[0]
                            : NxeDashboard.Shared.NxeStorageContract.PlatformFor(folderName, file.FileType);

                        await ExternalStorageManager.LogDiagnosticAsync($"[IMPORT] file='{file.Name}' extension='{file.FileType}' size={properties.Size} platform='{platform}'");

                        var candidate = new NxeFileCandidate
                        {
                            StorageDeviceId = drive.Id,
                            Platform = platform,
                            RelativePath = "/" + drive.VirtualName + "/Games/" + folderName + "/" + relative,
                            PhysicalPath = file.Path,
                            FileName = file.Name,
                            Extension = file.FileType,
                            Size = properties.Size,
                            ModifiedUtcTicks = properties.DateModified.UtcTicks,
                            ReadPrefixAsync = count => ReadPrefixAsync(file, count)
                        };

                        await ExternalStorageManager.LogDiagnosticAsync($"[IMPORT] prefix read begin file='{file.Name}'");
                        if (await library.IndexAsync(candidate, token))
                        {
                            imported++;
                            var record = library.LoadPage(5000, 0).FirstOrDefault(game =>
                                string.Equals(game.LaunchPath, file.Path, StringComparison.OrdinalIgnoreCase));
                            await ExternalStorageManager.LogDiagnosticAsync(
                                $"[IMPORT] accepted file='{file.Name}' signature='{record?.ContainerType ?? "unknown"}' " +
                                $"TitleId='{record?.NativeGameId ?? "unknown"}' DisplayName='{record?.CanonicalTitle ?? file.DisplayName}'");
                            await ExternalStorageManager.LogDiagnosticAsync($"[LIBRARY] game object created file='{file.Name}' platform='{platform}'");
                        }
                        else
                        {
                            var existing = library.LoadPage(5000, 0).FirstOrDefault(game =>
                                string.Equals(game.LaunchPath, file.Path, StringComparison.OrdinalIgnoreCase));
                            if (existing != null)
                            {
                                await ExternalStorageManager.LogDiagnosticAsync(
                                    $"[IMPORT] unchanged file='{file.Name}' signature='{existing.ContainerType ?? "unknown"}' " +
                                    $"TitleId='{existing.NativeGameId ?? "unknown"}' DisplayName='{existing.CanonicalTitle ?? file.DisplayName}'");
                            }
                            else
                            {
                                await ExternalStorageManager.LogDiagnosticAsync($"Candidate '{file.Name}' was rejected by importer registry");
                            }
                        }
                    }
                    catch (Exception ex) when (IsStorageException(ex))
                    {
                        await ExternalStorageManager.LogDiagnosticAsync($"File read error on '{file.Name}': {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        await ExternalStorageManager.LogDiagnosticAsync(
                            $"[IMPORT] failed file='{file.Name}' type='{ex.GetType().Name}' HRESULT=0x{ex.HResult:X8} message='{ex.Message}'");
                    }
                }
            }

            return imported;
        }

        private static async Task<byte[]> ReadPrefixAsync(StorageFile file, int count)
        {
            try
            {
                using (var stream = await file.OpenReadAsync())
                {
                    var length = (int)Math.Min(count, (long)stream.Size);
                    if (length <= 0) return new byte[0];
                    var buffer = new Windows.Storage.Streams.Buffer((uint)length);
                    var result = await stream.ReadAsync(buffer, (uint)length, InputStreamOptions.Partial);
                    var bytes = new byte[result.Length];
                    using (var reader = DataReader.FromBuffer(result))
                    {
                        reader.ReadBytes(bytes);
                        return bytes;
                    }
                }
            }
            catch (Exception ex) when (IsStorageException(ex))
            {
                await ExternalStorageManager.LogDiagnosticAsync(
                    $"[IMPORT] prefix read failed file='{file.Name}' type='{ex.GetType().Name}' HRESULT=0x{ex.HResult:X8} message='{ex.Message}'");
                return new byte[0];
            }
            catch (Exception ex)
            {
                await ExternalStorageManager.LogDiagnosticAsync(
                    $"[IMPORT] prefix read failed file='{file.Name}' type='{ex.GetType().Name}' HRESULT=0x{ex.HResult:X8} message='{ex.Message}'");
                return new byte[0];
            }
        }

        private static bool IsStorageException(Exception ex) =>
            ex is UnauthorizedAccessException || ex is System.IO.FileNotFoundException || ex is COMException;

        public void Dispose() => library.Dispose();
    }
}
