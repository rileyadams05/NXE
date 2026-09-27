using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NxeDashboard.Shared;
using NxeDashboard.Navigation;
using Windows.Storage;

namespace NxeDashboard.Runtime
{
    public sealed class StoragePresenceSnapshot
    {
        public int DriveCount { get; set; }
        public string Identity { get; set; }
        public bool ReadError { get; set; }
        public string Error { get; set; }
    }

    public sealed class ExternalDriveRegistration
    {
        public string Id { get; set; }
        public string VirtualName { get; set; }
        public StorageFolder DriveRoot { get; set; }
        public StorageFolder NxeRoot { get; set; }
    }

    public sealed class IndexedGame
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public string Platform { get; set; }
        public string Backend { get; set; }
        public string StorageRootId { get; set; }
        public string VirtualPath { get; set; }
        public ulong Size { get; set; }
    }

    public sealed class ExternalStorageManager
    {
        private static readonly SemaphoreSlim DiagnosticLogLock = new SemaphoreSlim(1, 1);
        private static readonly HashSet<string> SupportedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".iso", ".xex", ".bin", ".cue", ".chd", ".cso", ".pbp",
                ".gcm", ".wbfs", ".rvz", ".gdi", ".cdi", ".gba", ".gb",
                ".gbc", ".nes", ".sfc", ".smc", ".z64", ".n64", ".v64",
                ".md", ".gen", ".zip", ".7z", ".wad", ".live", ".con", ".pirs",
                ".elf", ".prx", ".nds", ".vb", ".sms", ".gg", ".sg", ".pce",
                ".a26", ".a52", ".a78", ".j64", ".jag", ".lnx", ".pcfx",
                ".ngp", ".ngc", ".ws", ".wsc", ".col", ".int", ".vec",
                ".o2", ".chf", ".min", "", "."
            };

        public static bool IsSupportedExtension(string extension)
        {
            var clean = extension ?? string.Empty;
            return clean == "." || clean == "" || SupportedExtensions.Contains(clean);
        }

        public async Task<StoragePresenceSnapshot> ProbePresenceAsync()
        {
            try
            {
                var drives = await KnownFolders.RemovableDevices.GetFoldersAsync();
                var keys = drives.Select(DriveKey).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
                await LogDiagnosticAsync("[STORAGE] RemovableDevices child count=" + drives.Count);
                return new StoragePresenceSnapshot
                {
                    DriveCount = drives.Count,
                    Identity = string.Join("|", keys),
                    ReadError = false
                };
            }
            catch (Exception exception)
            {
                await LogDiagnosticAsync("[STORAGE] RemovableDevices enumeration failed HRESULT=0x" +
                    exception.HResult.ToString("X8") + " error=" + exception.Message);
                return new StoragePresenceSnapshot
                {
                    DriveCount = 0,
                    Identity = string.Empty,
                    ReadError = true,
                    Error = exception.Message
                };
            }
        }

        public async Task<IReadOnlyList<ExternalDriveRegistration>> DiscoverAndPrepareAsync()
        {
            var result = new List<ExternalDriveRegistration>();
            IReadOnlyList<StorageFolder> drives;
            try
            {
                var removableRoot = KnownFolders.RemovableDevices;
                drives = await removableRoot.GetFoldersAsync();
                await LogDiagnosticAsync($"[STORAGE] RemovableDevices child count={drives?.Count ?? 0}");
            }
            catch (Exception ex)
            {
                await LogDiagnosticAsync($"KnownFolders.RemovableDevices error: {ex.Message}");
                return result;
            }

            if (drives == null || drives.Count == 0) return result;

            var ordered = drives.OrderBy(DriveKey, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var drive in ordered)
            {
                try
                {
                    await LogDiagnosticAsync($"[STORAGE] drive name='{drive.Name}' path='{drive.Path}'");
                    await drive.GetItemsAsync(0, 1);
                    await LogDiagnosticAsync($"[STORAGE] drive root readable=true name='{drive.Name}'");

                    // Search for official root-level layout first
                    StorageFolder nxeRoot = drive;
                    StorageFolder gamesFolder = null;
                    try { gamesFolder = await drive.GetFolderAsync("Games"); }
                    catch (Exception ex) when (IsDriveException(ex)) { }

                    if (gamesFolder == null)
                    {
                        try
                        {
                            var legacyNxe = await drive.GetFolderAsync("NXE");
                            var legacyGames = await legacyNxe.GetFolderAsync("Games");
                            if (legacyGames != null)
                            {
                                nxeRoot = legacyNxe;
                                gamesFolder = legacyGames;
                            }
                        }
                        catch (Exception ex) when (IsDriveException(ex)) { }
                    }

                    if (gamesFolder == null)
                    {
                        await LogDiagnosticAsync($"[STORAGE] Games exists=false drive='{drive.Name}'; initializing missing folders non-destructively");
                        await EnsureLayoutAsync(drive);
                    }
                    else
                    {
                        await LogDiagnosticAsync($"[STORAGE] Games exists=true drive='{drive.Name}'");
                    }

                    var stableId = await GetOrCreateStorageIdAsync(nxeRoot);
                    result.Add(new ExternalDriveRegistration
                    {
                        Id = stableId,
                        VirtualName = "usb" + result.Count,
                        DriveRoot = drive,
                        NxeRoot = nxeRoot
                    });
                }
                catch (Exception ex) when (IsDriveException(ex))
                {
                    await LogDiagnosticAsync($"Drive '{drive.Name}' access error: {ex.Message}");
                }
            }
            return result;
        }

        public async Task<IReadOnlyList<IndexedGame>> IndexAsync(
            IReadOnlyList<ExternalDriveRegistration> drives)
        {
            var games = new List<IndexedGame>();
            foreach (var drive in drives)
            {
                StorageFolder gameRoot = null;
                try { gameRoot = await drive.DriveRoot.GetFolderAsync("Games"); }
                catch (Exception ex) when (IsDriveException(ex)) { }

                if (gameRoot == null && drive.NxeRoot != null)
                {
                    try { gameRoot = await drive.NxeRoot.GetFolderAsync("Games"); }
                    catch (Exception ex) when (IsDriveException(ex)) { continue; }
                }

                if (gameRoot == null) continue;

                foreach (var folderName in NxeStorageContract.GameFolders)
                {
                    try
                    {
                        var folder = await gameRoot.GetFolderAsync(folderName);
                        await IndexFolderAsync(games, drive, folder, folderName, string.Empty);
                    }
                    catch (Exception ex) when (IsDriveException(ex)) { }
                }
            }
            return games.OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static async Task EnsureLayoutAsync(StorageFolder nxeRoot)
        {
            var folders = new Dictionary<string, StorageFolder>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in NxeStorageContract.TopLevel)
                folders[name] = await nxeRoot.CreateFolderAsync(name, CreationCollisionOption.OpenIfExists);

            foreach (var name in NxeStorageContract.DedicatedGameFolders)
                await folders["Games"].CreateFolderAsync(name, CreationCollisionOption.OpenIfExists);

            var retroArchGames = await folders["Games"].CreateFolderAsync("RetroArch", CreationCollisionOption.OpenIfExists);
            foreach (var sys in NxeStorageContract.RetroArchSystemFolders)
                await retroArchGames.CreateFolderAsync(sys, CreationCollisionOption.OpenIfExists);

            foreach (var name in NxeStorageContract.EmulatorFolders)
                await folders["EmulatorData"].CreateFolderAsync(name, CreationCollisionOption.OpenIfExists);

            foreach (var name in NxeStorageContract.BiosFolders)
                await folders["BIOS"].CreateFolderAsync(name, CreationCollisionOption.OpenIfExists);

            var retroArchBios = await folders["BIOS"].CreateFolderAsync("RetroArch", CreationCollisionOption.OpenIfExists);
            await retroArchBios.CreateFolderAsync("PPSSPP", CreationCollisionOption.OpenIfExists);
        }

        private static async Task IndexFolderAsync(List<IndexedGame> games,
            ExternalDriveRegistration drive, StorageFolder folder, string folderName, string relative)
        {
            IReadOnlyList<IStorageItem> items;
            try
            {
                items = await folder.GetItemsAsync();
            }
            catch (Exception ex) when (IsDriveException(ex)) { return; }

            foreach (var item in items)
            {
                if (item is StorageFolder childFolder)
                {
                    var childPath = string.IsNullOrEmpty(relative) ? childFolder.Name : relative + "/" + childFolder.Name;
                    await IndexFolderAsync(games, drive, childFolder, folderName, childPath);
                }
                else if (item is StorageFile file)
                {
                    if (file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                        !SupportedExtensions.Contains(file.FileType)) continue;
                    try
                    {
                        var platform = string.Equals(folderName, "RetroArch", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(relative)
                            ? relative.Split('/')[0]
                            : NxeStorageContract.PlatformFor(folderName, file.FileType);

                        var properties = await file.GetBasicPropertiesAsync();
                        var childPath = string.IsNullOrEmpty(relative) ? file.Name : relative + "/" + file.Name;
                        games.Add(new IndexedGame
                        {
                            Key = drive.Id + "|" + platform + "|" + folderName + "/" + childPath,
                            Title = System.IO.Path.GetFileNameWithoutExtension(file.Name),
                            Platform = platform,
                            Backend = NxeStorageContract.BackendFor(platform),
                            StorageRootId = drive.Id,
                            VirtualPath = "/" + drive.VirtualName + "/Games/" + folderName + "/" + childPath,
                            Size = properties.Size
                        });
                    }
                    catch (Exception ex) when (IsDriveException(ex)) { }
                }
            }
        }

        private static string DriveKey(StorageFolder folder) =>
            string.IsNullOrWhiteSpace(folder.Path) ? folder.Name : folder.Path;

        private static async Task<string> GetOrCreateStorageIdAsync(StorageFolder nxeRoot)
        {
            var config = await nxeRoot.CreateFolderAsync("Config", CreationCollisionOption.OpenIfExists);
            var file = await config.CreateFileAsync("storage.nxeid", CreationCollisionOption.OpenIfExists);
            try
            {
                var current = (await FileIO.ReadTextAsync(file)).Trim();
                if (Guid.TryParse(current, out var parsed)) return parsed.ToString("N");
            }
            catch (Exception ex) when (IsDriveException(ex)) { }
            var created = Guid.NewGuid().ToString("N");
            await FileIO.WriteTextAsync(file, created);
            return created;
        }

        public static async Task LogDiagnosticAsync(string message)
        {
            await DiagnosticLogLock.WaitAsync();
            try
            {
                System.Diagnostics.Debug.WriteLine("[NXE Storage] " + message);
                var local = ApplicationData.Current.LocalFolder;
                var logFile = await local.CreateFileAsync("storage_diag.txt", CreationCollisionOption.OpenIfExists);
                var entry = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message}\r\n";
                await FileIO.AppendTextAsync(logFile, entry);
            }
            catch { }
            finally { DiagnosticLogLock.Release(); }
        }

        private static bool IsDriveException(Exception ex) =>
            ex is UnauthorizedAccessException || ex is System.IO.FileNotFoundException || ex is COMException;
    }
}
