using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace NxeDashboard.Library
{
    public sealed class NxeSqliteRepository : IDisposable
    {
        private readonly object gate = new object();
        private IntPtr database;

        public NxeSqliteRepository(string path)
        {
            var bytes = Utf8(path);
            var result = Native.sqlite3_open_v2(bytes, out database,
                Native.SQLITE_OPEN_READWRITE | Native.SQLITE_OPEN_CREATE | Native.SQLITE_OPEN_FULLMUTEX, null);
            if (result != Native.SQLITE_OK) throw Error("Unable to open NXE database", result);
            Native.sqlite3_busy_timeout(database, 5000);
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
            CreateSchema();
        }

        public void CreateSchema()
        {
            Execute(@"
CREATE TABLE IF NOT EXISTS StorageDevices(
 StorageDeviceId TEXT PRIMARY KEY, DisplayName TEXT, VirtualRoot TEXT,
 IsAvailable INTEGER NOT NULL DEFAULT 0, LastSeenUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS Platforms(
 PlatformId TEXT PRIMARY KEY, DisplayName TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS Backends(
 BackendId TEXT PRIMARY KEY, DisplayName TEXT NOT NULL, IsAvailable INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS GameItems(
 InternalId TEXT PRIMARY KEY, CanonicalTitle TEXT NOT NULL, PlatformId TEXT NOT NULL,
 BackendId TEXT NOT NULL, NativeGameId TEXT, ContainerType TEXT, Region TEXT,
 Developer TEXT, Publisher TEXT, ReleaseDate TEXT, Description TEXT,
 CompatibilityStatus INTEGER NOT NULL DEFAULT 0, LastSeenUtc TEXT NOT NULL,
 FOREIGN KEY(PlatformId) REFERENCES Platforms(PlatformId),
 FOREIGN KEY(BackendId) REFERENCES Backends(BackendId));
CREATE TABLE IF NOT EXISTS GameFiles(
 GameFileId INTEGER PRIMARY KEY AUTOINCREMENT, GameId TEXT NOT NULL,
 StorageDeviceId TEXT NOT NULL, RelativePath TEXT NOT NULL, PhysicalPath TEXT, FileSize INTEGER NOT NULL,
 ModifiedUtcTicks INTEGER NOT NULL, IsAvailable INTEGER NOT NULL DEFAULT 1,
 ScanToken TEXT, UNIQUE(StorageDeviceId, RelativePath),
 FOREIGN KEY(GameId) REFERENCES GameItems(InternalId),
 FOREIGN KEY(StorageDeviceId) REFERENCES StorageDevices(StorageDeviceId));
CREATE TABLE IF NOT EXISTS Artwork(
 GameId TEXT PRIMARY KEY, CoverPath TEXT, BackgroundPath TEXT, IconPath TEXT,
 UpdatedUtc TEXT, FOREIGN KEY(GameId) REFERENCES GameItems(InternalId));
CREATE TABLE IF NOT EXISTS Compatibility(
 GameId TEXT NOT NULL, BackendId TEXT NOT NULL, Status INTEGER NOT NULL DEFAULT 0,
 Notes TEXT, BackendVersion TEXT, UpdatedUtc TEXT,
 PRIMARY KEY(GameId, BackendId));
CREATE TABLE IF NOT EXISTS Favorites(
 GameId TEXT PRIMARY KEY, IsFavorite INTEGER NOT NULL DEFAULT 0,
 UpdatedUtc TEXT NOT NULL, FOREIGN KEY(GameId) REFERENCES GameItems(InternalId));
CREATE TABLE IF NOT EXISTS PlayHistory(
 GameId TEXT PRIMARY KEY, PlayCount INTEGER NOT NULL DEFAULT 0,
 LastPlayedUtc TEXT, FOREIGN KEY(GameId) REFERENCES GameItems(InternalId));
CREATE INDEX IF NOT EXISTS IX_GameItems_Title ON GameItems(CanonicalTitle COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS IX_GameItems_Platform ON GameItems(PlatformId);
CREATE INDEX IF NOT EXISTS IX_GameItems_Backend ON GameItems(BackendId);
CREATE INDEX IF NOT EXISTS IX_GameFiles_Game ON GameFiles(GameId);
CREATE INDEX IF NOT EXISTS IX_GameFiles_StorageAvailable ON GameFiles(StorageDeviceId, IsAvailable);
CREATE INDEX IF NOT EXISTS IX_PlayHistory_Recent ON PlayHistory(LastPlayedUtc DESC);");
            SeedDefinitions();
            try { Execute("ALTER TABLE GameFiles ADD COLUMN PhysicalPath TEXT;"); }
            catch (InvalidOperationException) { /* Existing databases already have the column. */ }
        }

        private void SeedDefinitions()
        {
            foreach (var platform in NxeDashboard.Shared.NxeStorageContract.RetroArchSystemFolders)
            {
                Execute("INSERT OR IGNORE INTO Platforms VALUES(" + Q(platform) + "," + Q(platform) + ");");
            }
            // Importers store canonical platform IDs (Xbox360, PS2, GameCube, Wii,
            // Dreamcast), not only their emulator folder names. Seed both forms so
            // GameItems never references a platform row that does not exist.
            foreach (var platform in NxeDashboard.Shared.NxeStorageContract.GameFolders)
            {
                Execute("INSERT OR IGNORE INTO Platforms VALUES(" + Q(platform) + "," + Q(platform) + ");");
            }
            foreach (var pair in new[]
            {
                "xenia|Xenia", "xbsx2|XBSX2", "dolphin|Dolphin",
                "flycast|Flycast", "retroarch|RetroArch"
            })
            {
                var fields = pair.Split('|');
                Execute("INSERT OR IGNORE INTO Backends VALUES(" + Q(fields[0]) + "," + Q(fields[1]) + ",0);");
            }
        }

        public void SetAllStorageOffline()
        {
            Execute("BEGIN; UPDATE StorageDevices SET IsAvailable=0; UPDATE GameFiles SET IsAvailable=0; COMMIT;");
        }

        public void UpsertStorage(NxeStorageDevice storage)
        {
            Execute("INSERT INTO StorageDevices(StorageDeviceId,DisplayName,VirtualRoot,IsAvailable,LastSeenUtc) VALUES(" +
                Q(storage.StorageDeviceId) + "," + Q(storage.DisplayName) + "," + Q(storage.VirtualRoot) + "," +
                B(storage.IsAvailable) + "," + Q(Iso(storage.LastSeen)) + ") ON CONFLICT(StorageDeviceId) DO UPDATE SET " +
                "DisplayName=excluded.DisplayName,VirtualRoot=excluded.VirtualRoot,IsAvailable=excluded.IsAvailable,LastSeenUtc=excluded.LastSeenUtc;");
            if (storage.IsAvailable)
                Execute("UPDATE GameFiles SET IsAvailable=1 WHERE StorageDeviceId=" + Q(storage.StorageDeviceId) + ";");
        }

        public string BeginScan(string storageDeviceId)
        {
            var token = Guid.NewGuid().ToString("N");
            Execute("UPDATE GameFiles SET ScanToken=NULL WHERE StorageDeviceId=" + Q(storageDeviceId) + ";");
            return token;
        }

        public bool IsUnchanged(NxeFileCandidate file)
        {
            var rows = Query("SELECT FileSize,ModifiedUtcTicks FROM GameFiles WHERE StorageDeviceId=" +
                Q(file.StorageDeviceId) + " AND RelativePath=" + Q(file.RelativePath) + " LIMIT 1;");
            return rows.Count == 1 && U64(rows[0][0]) == file.Size && L64(rows[0][1]) == file.ModifiedUtcTicks;
        }

        public void MarkSeen(NxeFileCandidate file, string scanToken)
        {
            Execute("UPDATE GameFiles SET IsAvailable=1,ScanToken=" + Q(scanToken) +
                " WHERE StorageDeviceId=" + Q(file.StorageDeviceId) + " AND RelativePath=" + Q(file.RelativePath) + ";");
        }

        public void UpsertGame(NxeGameRecord game, NxeFileCandidate file, string scanToken)
        {
            Execute("BEGIN; INSERT INTO GameItems(InternalId,CanonicalTitle,PlatformId,BackendId,NativeGameId,ContainerType,Region,Developer,Publisher,ReleaseDate,Description,CompatibilityStatus,LastSeenUtc) VALUES(" +
                Q(game.InternalId) + "," + Q(game.CanonicalTitle) + "," + Q(game.Platform) + "," + Q(game.BackendId) + "," +
                Q(game.NativeGameId) + "," + Q(game.ContainerType) + "," + Q(game.Region) + "," + Q(game.Developer) + "," +
                Q(game.Publisher) + "," + Q(game.ReleaseDate.HasValue ? Iso(game.ReleaseDate.Value) : null) + "," + Q(game.Description) + "," +
                ((int)game.CompatibilityStatus).ToString(CultureInfo.InvariantCulture) + "," + Q(Iso(game.LastSeen)) +
                ") ON CONFLICT(InternalId) DO UPDATE SET CanonicalTitle=excluded.CanonicalTitle,PlatformId=excluded.PlatformId," +
                "BackendId=excluded.BackendId,NativeGameId=COALESCE(excluded.NativeGameId,GameItems.NativeGameId)," +
                "ContainerType=excluded.ContainerType,Region=COALESCE(excluded.Region,GameItems.Region),LastSeenUtc=excluded.LastSeenUtc;" +
                "INSERT INTO GameFiles(GameId,StorageDeviceId,RelativePath,PhysicalPath,FileSize,ModifiedUtcTicks,IsAvailable,ScanToken) VALUES(" +
                Q(game.InternalId) + "," + Q(file.StorageDeviceId) + "," + Q(file.RelativePath) + "," + Q(file.PhysicalPath) + "," + N(file.Size) + "," +
                file.ModifiedUtcTicks.ToString(CultureInfo.InvariantCulture) + ",1," + Q(scanToken) +
                ") ON CONFLICT(StorageDeviceId,RelativePath) DO UPDATE SET GameId=excluded.GameId,FileSize=excluded.FileSize," +
                "PhysicalPath=excluded.PhysicalPath,ModifiedUtcTicks=excluded.ModifiedUtcTicks,IsAvailable=1,ScanToken=excluded.ScanToken;" +
                "INSERT OR IGNORE INTO Favorites(GameId,IsFavorite,UpdatedUtc) VALUES(" + Q(game.InternalId) + ",0," + Q(Iso(DateTimeOffset.UtcNow)) + ");" +
                "INSERT OR IGNORE INTO PlayHistory(GameId,PlayCount,LastPlayedUtc) VALUES(" + Q(game.InternalId) + ",0,NULL); COMMIT;");
        }

        public void FinishScan(string storageDeviceId, string scanToken)
        {
            Execute("BEGIN; UPDATE GameFiles SET IsAvailable=0 WHERE StorageDeviceId=" + Q(storageDeviceId) +
                " AND (ScanToken IS NULL OR ScanToken<>" + Q(scanToken) + "); COMMIT;");
        }

        public void DeleteFile(string storageDeviceId, string relativePath)
        {
            Execute("UPDATE GameFiles SET IsAvailable=0 WHERE StorageDeviceId=" + Q(storageDeviceId) +
                " AND RelativePath=" + Q(relativePath) + ";");
        }

        public void RenameFile(string storageDeviceId, string oldPath, string newPath)
        {
            Execute("UPDATE GameFiles SET RelativePath=" + Q(newPath) + " WHERE StorageDeviceId=" +
                Q(storageDeviceId) + " AND RelativePath=" + Q(oldPath) + ";");
        }

        public void SetFavorite(string gameId, bool favorite)
        {
            Execute("INSERT INTO Favorites(GameId,IsFavorite,UpdatedUtc) VALUES(" + Q(gameId) + "," + B(favorite) + "," +
                Q(Iso(DateTimeOffset.UtcNow)) + ") ON CONFLICT(GameId) DO UPDATE SET IsFavorite=excluded.IsFavorite,UpdatedUtc=excluded.UpdatedUtc;");
        }

        public void RecordLaunch(string gameId, DateTimeOffset when)
        {
            Execute("INSERT INTO PlayHistory(GameId,PlayCount,LastPlayedUtc) VALUES(" + Q(gameId) + ",1," + Q(Iso(when)) +
                ") ON CONFLICT(GameId) DO UPDATE SET PlayCount=PlayHistory.PlayCount+1,LastPlayedUtc=excluded.LastPlayedUtc;");
        }

        public void SetArtwork(string gameId, string coverPath, string backgroundPath, string iconPath)
        {
            Execute("INSERT INTO Artwork(GameId,CoverPath,BackgroundPath,IconPath,UpdatedUtc) VALUES(" +
                Q(gameId) + "," + Q(coverPath) + "," + Q(backgroundPath) + "," + Q(iconPath) + "," +
                Q(Iso(DateTimeOffset.UtcNow)) + ") ON CONFLICT(GameId) DO UPDATE SET CoverPath=excluded.CoverPath," +
                "BackgroundPath=excluded.BackgroundPath,IconPath=excluded.IconPath,UpdatedUtc=excluded.UpdatedUtc;");
        }

        public IReadOnlyList<NxeGameRecord> LoadPage(int limit, int offset, bool availableOnly = false)
        {
            var sql = @"SELECT g.InternalId,g.CanonicalTitle,g.PlatformId,g.BackendId,g.NativeGameId,
COALESCE((SELECT f.RelativePath FROM GameFiles f WHERE f.GameId=g.InternalId ORDER BY f.IsAvailable DESC,f.GameFileId LIMIT 1),''),
COALESCE((SELECT f.PhysicalPath FROM GameFiles f WHERE f.GameId=g.InternalId ORDER BY f.IsAvailable DESC,f.GameFileId LIMIT 1),''),
COALESCE((SELECT f.StorageDeviceId FROM GameFiles f WHERE f.GameId=g.InternalId ORDER BY f.IsAvailable DESC,f.GameFileId LIMIT 1),''),
CASE WHEN EXISTS(SELECT 1 FROM GameFiles f WHERE f.GameId=g.InternalId AND f.IsAvailable=1) THEN 1 ELSE 0 END,
g.ContainerType,g.Region,g.Developer,g.Publisher,g.ReleaseDate,g.Description,
a.CoverPath,a.BackgroundPath,a.IconPath,g.CompatibilityStatus,
COALESCE(v.IsFavorite,0),h.LastPlayedUtc,COALESCE(h.PlayCount,0),g.LastSeenUtc,
COALESCE((SELECT f.FileSize FROM GameFiles f WHERE f.GameId=g.InternalId ORDER BY f.IsAvailable DESC,f.GameFileId LIMIT 1),0),
COALESCE((SELECT f.ModifiedUtcTicks FROM GameFiles f WHERE f.GameId=g.InternalId ORDER BY f.IsAvailable DESC,f.GameFileId LIMIT 1),0)
FROM GameItems g LEFT JOIN Artwork a ON a.GameId=g.InternalId
LEFT JOIN Favorites v ON v.GameId=g.InternalId LEFT JOIN PlayHistory h ON h.GameId=g.InternalId " +
                (availableOnly ? "WHERE EXISTS(SELECT 1 FROM GameFiles f WHERE f.GameId=g.InternalId AND f.IsAvailable=1) " : string.Empty) +
                "ORDER BY v.IsFavorite DESC,CASE WHEN h.LastPlayedUtc IS NULL THEN 1 ELSE 0 END,h.LastPlayedUtc DESC,g.CanonicalTitle COLLATE NOCASE LIMIT " +
                Math.Max(1, limit) + " OFFSET " + Math.Max(0, offset) + ";";
            return Query(sql).Select(ToGame).ToList();
        }

        public int CountGames() => int.Parse(Query("SELECT COUNT(*) FROM GameItems;")[0][0], CultureInfo.InvariantCulture);

        private NxeGameRecord ToGame(string[] row)
        {
            return new NxeGameRecord
            {
                InternalId=row[0], CanonicalTitle=row[1], Platform=row[2], BackendId=row[3], NativeGameId=Null(row[4]),
                PrimaryPath=row[5], LaunchPath=Null(row[6]), StorageDeviceId=row[7], IsAvailable=row[8]=="1", ContainerType=Null(row[9]), Region=Null(row[10]),
                Developer=Null(row[11]), Publisher=Null(row[12]), ReleaseDate=Date(row[13]), Description=Null(row[14]),
                CoverPath=Null(row[15]), BackgroundPath=Null(row[16]), IconPath=Null(row[17]),
                CompatibilityStatus=(NxeCompatibilityStatus)int.Parse(row[18]), IsFavorite=row[19]=="1",
                LastPlayed=Date(row[20]), PlayCount=L64(row[21]), LastSeen=Date(row[22]) ?? DateTimeOffset.MinValue,
                FileSize=U64(row[23]), ModifiedUtcTicks=L64(row[24])
            };
        }

        private void Execute(string sql)
        {
            lock (gate)
            {
                IntPtr error;
                var result = Native.sqlite3_exec(database, Utf8(sql), null, IntPtr.Zero, out error);
                if (result != Native.SQLITE_OK)
                {
                    var message = error == IntPtr.Zero ? null : Ptr(error);
                    if (error != IntPtr.Zero) Native.sqlite3_free(error);
                    throw Error(message ?? "SQLite command failed", result);
                }
            }
        }

        private List<string[]> Query(string sql)
        {
            lock (gate)
            {
                var rows = new List<string[]>();
                Native.ExecCallback callback = (context, count, values, names) =>
                {
                    var row = new string[count];
                    for (var index = 0; index < count; index++)
                    {
                        var value = Marshal.ReadIntPtr(values, index * IntPtr.Size);
                        row[index] = value == IntPtr.Zero ? string.Empty : Ptr(value);
                    }
                    rows.Add(row); return 0;
                };
                IntPtr error;
                var result = Native.sqlite3_exec(database, Utf8(sql), callback, IntPtr.Zero, out error);
                GC.KeepAlive(callback);
                if (result != Native.SQLITE_OK)
                {
                    var message = error == IntPtr.Zero ? null : Ptr(error);
                    if (error != IntPtr.Zero) Native.sqlite3_free(error);
                    throw Error(message ?? "SQLite query failed", result);
                }
                return rows;
            }
        }

        private Exception Error(string prefix, int result) => new InvalidOperationException(prefix + " (SQLite " + result + "): " +
            (database == IntPtr.Zero ? string.Empty : Ptr(Native.sqlite3_errmsg(database))));
        private static byte[] Utf8(string value) => EncodingWithNull(value ?? string.Empty);
        private static byte[] EncodingWithNull(string value)
        {
            var source = System.Text.Encoding.UTF8.GetBytes(value); var result = new byte[source.Length + 1];
            Buffer.BlockCopy(source, 0, result, 0, source.Length); return result;
        }
        private static string Ptr(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return null;
            var length = 0;
            while (Marshal.ReadByte(pointer, length) != 0) length++;
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        private static string Q(string value) => value == null ? "NULL" : "'" + value.Replace("'", "''") + "'";
        private static string B(bool value) => value ? "1" : "0";
        private static string N(ulong value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        private static string Null(string value) => string.IsNullOrEmpty(value) ? null : value;
        private static DateTimeOffset? Date(string value) { if (string.IsNullOrEmpty(value)) return null; return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture); }
        private static long L64(string value) => string.IsNullOrEmpty(value) ? 0 : long.Parse(value, CultureInfo.InvariantCulture);
        private static ulong U64(string value) => string.IsNullOrEmpty(value) ? 0 : ulong.Parse(value, CultureInfo.InvariantCulture);

        public void Dispose()
        {
            lock (gate) { if (database != IntPtr.Zero) { Native.sqlite3_close_v2(database); database = IntPtr.Zero; } }
        }

        private static class Native
        {
            internal const int SQLITE_OK=0, SQLITE_OPEN_READWRITE=2, SQLITE_OPEN_CREATE=4, SQLITE_OPEN_FULLMUTEX=0x10000;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ExecCallback(IntPtr context, int count, IntPtr values, IntPtr names);
            [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, byte[] vfs);
            [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(IntPtr db);
            [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
            [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_exec(IntPtr db, byte[] sql, ExecCallback callback, IntPtr context, out IntPtr error);
            [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg(IntPtr db);
            [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern void sqlite3_free(IntPtr value);
        }
    }
}
