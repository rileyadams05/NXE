using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NxeDashboard.Library
{
    public sealed class NxeLibraryService : IDisposable
    {
        private readonly NxeSqliteRepository repository;
        private readonly NxeImporterRegistry importers;
        public event EventHandler LibraryChanged;

        public NxeLibraryService(string databasePath)
        {
            repository = new NxeSqliteRepository(databasePath);
            importers = new NxeImporterRegistry();
        }

        public IReadOnlyList<NxeGameRecord> LoadPage(int limit = 500, int offset = 0) =>
            repository.LoadPage(limit, offset);
        public int Count => repository.CountGames();
        public IReadOnlyList<INxeGameImporter> Importers => importers.All;

        public void BeginStorageDiscovery() => repository.SetAllStorageOffline();
        public void RegisterStorage(NxeStorageDevice storage) => repository.UpsertStorage(storage);
        public string BeginScan(string storageDeviceId) => repository.BeginScan(storageDeviceId);

        public async Task<bool> IndexAsync(NxeFileCandidate candidate, string scanToken)
        {
            if (repository.IsUnchanged(candidate))
            {
                repository.MarkSeen(candidate, scanToken);
                return false;
            }
            var importer = importers.For(candidate);
            if (importer == null) return false;
            var game = await importer.ImportAsync(candidate);
            if (game == null) return false;
            repository.UpsertGame(game, candidate, scanToken);
            return true;
        }

        public void FinishScan(string storageDeviceId, string scanToken)
        {
            repository.FinishScan(storageDeviceId, scanToken);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }

        public void FileDeleted(string storageDeviceId, string path)
        {
            repository.DeleteFile(storageDeviceId, path);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }

        public void FileRenamed(string storageDeviceId, string oldPath, string newPath)
        {
            repository.RenameFile(storageDeviceId, oldPath, newPath);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetFavorite(string gameId, bool favorite)
        {
            repository.SetFavorite(gameId, favorite);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RecordLaunch(string gameId, DateTimeOffset when)
        {
            repository.RecordLaunch(gameId, when);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetArtwork(string gameId, string coverPath, string backgroundPath, string iconPath)
        {
            repository.SetArtwork(gameId, coverPath, backgroundPath, iconPath);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() => repository.Dispose();
    }
}
