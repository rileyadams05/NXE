// WinRT/UWP port of the protocol behavior in the MIT-licensed uFTP reference
// retained at Dashboard/FTP. This file never exposes paths outside NXE roots.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NxeDashboard.Shared;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Storage;
using Windows.Storage.Streams;

namespace NxeDashboard.Runtime
{
    public sealed class NxeFtpServer : IDisposable
    {
        private readonly object sync = new object();
        private readonly HashSet<StreamSocket> clients = new HashSet<StreamSocket>();
        private StreamSocketListener listener;
        private string password;

        public bool IsRunning { get; private set; }
        public string Port { get; private set; }
        public string Username { get; private set; }
        public string LastActivity { get; private set; } = "No transfers yet";
        public int ActiveConnections { get { lock (sync) return clients.Count; } }
        public event EventHandler StateChanged;
        public event EventHandler StorageChanged;

        public async Task StartAsync(string port, string username, string suppliedPassword)
        {
            if (IsRunning) return;
            await EnsureRootsAsync();
            listener = new StreamSocketListener();
            listener.ConnectionReceived += OnConnection;
            try
            {
                await listener.BindServiceNameAsync(port);
                Port = listener.Information.LocalPort;
                Username = username ?? string.Empty;
                password = suppliedPassword ?? string.Empty;
                LastActivity = "Ready";
                IsRunning = true;
                NotifyState();
            }
            catch
            {
                listener.ConnectionReceived -= OnConnection;
                listener.Dispose();
                listener = null;
                throw;
            }
        }

        public void Stop()
        {
            if (listener != null)
            {
                listener.ConnectionReceived -= OnConnection;
                listener.Dispose();
                listener = null;
            }
            lock (sync)
            {
                foreach (var client in clients) client.Dispose();
                clients.Clear();
            }
            IsRunning = false;
            Port = Username = password = null;
            LastActivity = "Stopped";
            NotifyState();
        }

        private async void OnConnection(
            StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            var socket = args.Socket;
            lock (sync) clients.Add(socket);
            LastActivity = "Client connected";
            NotifyState();
            try { await new Session(this, socket).RunAsync(); }
            catch { }
            finally
            {
                lock (sync) clients.Remove(socket);
                socket.Dispose();
                LastActivity = "Client disconnected";
                NotifyState();
            }
        }

        private sealed class Session
        {
            private readonly NxeFtpServer server;
            private readonly StreamSocket control;
            private string cwd = "/";
            private bool loggedIn;
            private bool userAccepted;
            private ulong restart;
            private ulong? allocation;
            private string renameFrom;
            private PassiveChannel passive;

            public Session(NxeFtpServer server, StreamSocket control)
            {
                this.server = server;
                this.control = control;
                var auth = !string.IsNullOrEmpty(server.Username) || !string.IsNullOrEmpty(server.password);
                loggedIn = userAccepted = !auth;
            }

            public async Task RunAsync()
            {
                using (var reader = new DataReader(control.InputStream))
                using (var writer = new DataWriter(control.OutputStream))
                {
                    reader.InputStreamOptions = InputStreamOptions.Partial;
                    await Reply(writer, "220 NXE FTP ready");
                    while (server.IsRunning)
                    {
                        var line = await ReadLine(reader);
                        if (line == null || !await Execute(writer, line)) break;
                    }
                }
                passive?.Dispose();
            }

            private async Task<bool> Execute(DataWriter writer, string line)
            {
                var split = line.IndexOf(' ');
                var command = (split < 0 ? line : line.Substring(0, split)).Trim().ToUpperInvariant();
                var arg = split < 0 ? string.Empty : line.Substring(split + 1).Trim();
                if (command == "USER")
                {
                    var auth = !string.IsNullOrEmpty(server.Username) || !string.IsNullOrEmpty(server.password);
                    userAccepted = !auth || string.Equals(arg, server.Username, StringComparison.Ordinal);
                    loggedIn = !auth && userAccepted;
                    await Reply(writer, userAccepted ? (auth ? "331 Password required" : "230 Logged in") : "530 Invalid username");
                    return true;
                }
                if (command == "PASS")
                {
                    var auth = !string.IsNullOrEmpty(server.Username) || !string.IsNullOrEmpty(server.password);
                    loggedIn = userAccepted && (!auth || string.Equals(arg, server.password, StringComparison.Ordinal));
                    await Reply(writer, loggedIn ? "230 Logged in" : "530 Login incorrect");
                    return true;
                }
                if (command == "QUIT") { await Reply(writer, "221 Goodbye"); return false; }
                if (command == "SYST") { await Reply(writer, "215 UNIX Type: L8"); return true; }
                if (command == "FEAT")
                {
                    await Reply(writer, "211-Features\r\n UTF8\r\n EPSV\r\n PASV\r\n MLSD\r\n SIZE\r\n MDTM\r\n REST STREAM\r\n211 End");
                    return true;
                }
                if (command == "NOOP" || command == "CLNT") { await Reply(writer, "200 OK"); return true; }
                if (!loggedIn) { await Reply(writer, "530 Not logged in"); return true; }
                try
                {
                    switch (command)
                    {
                        case "TYPE": case "MODE": case "STRU": await Reply(writer, "200 Accepted"); break;
                        case "OPTS": await Reply(writer, arg.Equals("UTF8 ON", StringComparison.OrdinalIgnoreCase) ? "200 UTF8 enabled" : "501 Unsupported option"); break;
                        case "PWD": case "XPWD": await Reply(writer, "257 \"" + cwd + "\""); break;
                        case "CWD": await ChangeDirectory(writer, arg); break;
                        case "CDUP": await ChangeDirectory(writer, ".."); break;
                        case "PASV": await Passive(writer, false); break;
                        case "EPSV": await Passive(writer, true); break;
                        case "LIST": await List(writer, arg, false, false); break;
                        case "NLST": await List(writer, arg, true, false); break;
                        case "MLSD": await List(writer, arg, false, true); break;
                        case "RETR": await Retrieve(writer, arg); break;
                        case "STOR": await Store(writer, arg, false); break;
                        case "APPE": await Store(writer, arg, true); break;
                        case "REST":
                            if (ulong.TryParse(arg, out var offset)) { restart = offset; await Reply(writer, "350 Restart accepted"); }
                            else await Reply(writer, "501 Invalid offset");
                            break;
                        case "ALLO":
                            if (ulong.TryParse(arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var bytes))
                            { allocation = bytes; await Reply(writer, "200 Allocation accepted"); }
                            else await Reply(writer, "501 Invalid allocation");
                            break;
                        case "SIZE": await Size(writer, arg); break;
                        case "MDTM": await Modified(writer, arg); break;
                        case "MKD": case "XMKD": await MakeDirectory(writer, arg); break;
                        case "DELE": await Delete(writer, arg, false); break;
                        case "RMD": case "XRMD": await Delete(writer, arg, true); break;
                        case "RNFR": await RenameStart(writer, arg); break;
                        case "RNTO": await RenameFinish(writer, arg); break;
                        case "ABOR": passive?.Dispose(); passive = null; await Reply(writer, "226 Aborted"); break;
                        case "STAT": await Reply(writer, "211 NXE FTP running; directory " + cwd); break;
                        case "PORT": case "EPRT": await Reply(writer, "502 Use PASV or EPSV"); break;
                        case "AUTH": case "PBSZ": case "PROT": await Reply(writer, "502 TLS not configured"); break;
                        default: await Reply(writer, "502 Command not implemented"); break;
                    }
                }
                catch (UnauthorizedAccessException) { await Reply(writer, "550 Access denied"); }
                catch (System.IO.FileNotFoundException) { await Reply(writer, "550 Not found"); }
                catch (ArgumentException) { await Reply(writer, "550 Invalid path"); }
                catch (TimeoutException) { await Reply(writer, "425 Data connection timeout"); }
                catch (Exception ex)
                {
                    server.LastActivity = "FTP error: " + ex.Message;
                    server.NotifyState();
                    await Reply(writer, "451 Local error");
                }
                return true;
            }

            private async Task ChangeDirectory(DataWriter writer, string requested)
            {
                var path = Normalize(cwd, requested);
                if (path != "/" && await server.Folder(path) == null) { await Reply(writer, "550 Directory not found"); return; }
                cwd = path;
                await Reply(writer, "250 Directory changed");
            }

            private async Task Passive(DataWriter writer, bool extended)
            {
                passive?.Dispose();
                passive = new PassiveChannel();
                await passive.StartAsync();
                if (extended) { await Reply(writer, "229 Entering Extended Passive Mode (|||" + passive.Port + "|)"); return; }
                var address = control.Information.LocalAddress?.CanonicalName;
                if (string.IsNullOrEmpty(address) || !address.Contains(".")) address = Addresses().FirstOrDefault();
                if (string.IsNullOrEmpty(address)) { passive.Dispose(); passive = null; await Reply(writer, "522 Use EPSV"); return; }
                var port = int.Parse(passive.Port, CultureInfo.InvariantCulture);
                await Reply(writer, "227 Entering Passive Mode (" + address.Replace('.', ',') + "," + port / 256 + "," + port % 256 + ")");
            }

            private async Task<StreamSocket> Data(DataWriter writer)
            {
                if (passive == null) { await Reply(writer, "425 Use PASV or EPSV first"); return null; }
                await Reply(writer, "150 Opening data connection");
                try { return await passive.AcceptAsync(); }
                finally { passive.Dispose(); passive = null; }
            }

            private async Task List(DataWriter writer, string arg, bool namesOnly, bool machine)
            {
                var path = Normalize(cwd, StripOptions(arg));
                var lines = await server.Listing(path, namesOnly, machine);
                if (lines == null) { await Reply(writer, "550 Not found"); return; }
                var socket = await Data(writer); if (socket == null) return;
                using (socket)
                using (var output = new DataWriter(socket.OutputStream))
                {
                    output.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                    output.WriteString(string.Join("\r\n", lines) + (lines.Count > 0 ? "\r\n" : string.Empty));
                    await output.StoreAsync();
                }
                server.Activity("Listed " + path, false);
                await Reply(writer, "226 Transfer complete");
            }

            private async Task Retrieve(DataWriter writer, string arg)
            {
                var path = Normalize(cwd, arg);
                var file = await server.Item(path) as StorageFile;
                if (file == null) { await Reply(writer, "550 File not found"); return; }
                var socket = await Data(writer); if (socket == null) return;
                using (socket)
                using (var stream = await file.OpenReadAsync())
                using (var input = stream.GetInputStreamAt(Math.Min(restart, stream.Size)))
                {
                    await RandomAccessStream.CopyAsync(input, socket.OutputStream);
                    await socket.OutputStream.FlushAsync();
                }
                restart = 0;
                server.Activity("Downloaded " + path, false);
                await Reply(writer, "226 Transfer complete");
            }

            private async Task Store(DataWriter writer, string arg, bool append)
            {
                var path = Normalize(cwd, arg);
                var target = await server.Parent(path);
                if (target == null) { await Reply(writer, "550 Parent not found"); return; }
                var socket = await Data(writer); if (socket == null) return;
                var partName = target.Name + ".part";
                var part = await target.Folder.CreateFileAsync(partName,
                    append || restart > 0 ? CreationCollisionOption.OpenIfExists : CreationCollisionOption.ReplaceExisting);
                try
                {
                    using (socket)
                    using (var stream = await part.OpenAsync(FileAccessMode.ReadWrite))
                    {
                        var offset = append ? stream.Size : restart;
                        if (!append && restart == 0) stream.Size = 0;
                        if (offset > stream.Size) offset = stream.Size;
                        using (var output = stream.GetOutputStreamAt(offset))
                        {
                            await RandomAccessStream.CopyAsync(socket.InputStream, output);
                            await output.FlushAsync();
                        }
                        var received = stream.Size - offset;
                        if (allocation.HasValue && received != allocation.Value)
                            throw new System.IO.InvalidDataException(
                                "Upload size mismatch: expected " + allocation.Value + ", received " + received + ".");
                    }
                    // Do not publish the final filename after the client session
                    // has disappeared. A data-channel EOF alone may be graceful
                    // even when the overall FTP upload was interrupted.
                    await control.OutputStream.FlushAsync();
                    var existing = await target.Folder.TryGetItemAsync(target.Name);
                    if (existing != null) await existing.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    await part.RenameAsync(target.Name, NameCollisionOption.ReplaceExisting);
                }
                catch { throw; }
                finally { restart = 0; allocation = null; }
                server.Activity("Uploaded " + path, true);
                await Reply(writer, "226 Transfer complete");
            }

            private async Task Size(DataWriter writer, string arg)
            {
                var file = await server.Item(Normalize(cwd, arg)) as StorageFile;
                if (file == null) { await Reply(writer, "550 File not found"); return; }
                await Reply(writer, "213 " + (await file.GetBasicPropertiesAsync()).Size);
            }

            private async Task Modified(DataWriter writer, string arg)
            {
                var item = await server.Item(Normalize(cwd, arg));
                if (item == null) { await Reply(writer, "550 Not found"); return; }
                var when = item.DateCreated.UtcDateTime;
                if (item is StorageFile file) when = (await file.GetBasicPropertiesAsync()).DateModified.UtcDateTime;
                await Reply(writer, "213 " + when.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
            }

            private async Task MakeDirectory(DataWriter writer, string arg)
            {
                var path = Normalize(cwd, arg); var target = await server.Parent(path);
                if (target == null) { await Reply(writer, "550 Parent not found"); return; }
                await target.Folder.CreateFolderAsync(target.Name, CreationCollisionOption.FailIfExists);
                server.Activity("Created " + path, true);
                await Reply(writer, "257 \"" + path + "\" created");
            }

            private async Task Delete(DataWriter writer, string arg, bool directory)
            {
                var path = Normalize(cwd, arg);
                if (Parts(path).Count <= 1) { await Reply(writer, "550 Virtual roots cannot be removed"); return; }
                var item = await server.Item(path);
                if (item == null || directory != item.IsOfType(StorageItemTypes.Folder)) { await Reply(writer, "550 Not found"); return; }
                if (directory && (await ((StorageFolder)item).GetItemsAsync()).Count != 0)
                { await Reply(writer, "550 Directory is not empty"); return; }
                await item.DeleteAsync(StorageDeleteOption.PermanentDelete);
                server.Activity("Deleted " + path, true);
                await Reply(writer, "250 Deleted");
            }

            private async Task RenameStart(DataWriter writer, string arg)
            {
                var path = Normalize(cwd, arg);
                if (Parts(path).Count <= 1) { await Reply(writer, "550 Virtual roots cannot be renamed"); return; }
                if (await server.Item(path) == null) { await Reply(writer, "550 Not found"); return; }
                renameFrom = path; await Reply(writer, "350 Ready for RNTO");
            }

            private async Task RenameFinish(DataWriter writer, string arg)
            {
                if (string.IsNullOrEmpty(renameFrom)) { await Reply(writer, "503 RNFR required"); return; }
                var to = Normalize(cwd, arg);
                var source = await server.Item(renameFrom);
                if (source == null) { renameFrom = null; await Reply(writer, "550 Not found"); return; }
                if (string.Equals(ParentPath(renameFrom), ParentPath(to), StringComparison.OrdinalIgnoreCase))
                {
                    await source.RenameAsync(FileName(to), NameCollisionOption.FailIfExists);
                }
                else if (source is StorageFile sourceFile)
                {
                    var destination = await server.Parent(to);
                    if (destination == null) { await Reply(writer, "550 Destination not found"); return; }
                    await sourceFile.MoveAsync(destination.Folder, destination.Name, NameCollisionOption.FailIfExists);
                }
                else
                {
                    await Reply(writer, "550 Moving folders between directories is unsupported");
                    return;
                }
                server.Activity("Renamed " + renameFrom + " to " + to, true);
                renameFrom = null; await Reply(writer, "250 Renamed");
            }
        }

        private sealed class PassiveChannel : IDisposable
        {
            private StreamSocketListener listener;
            private TaskCompletionSource<StreamSocket> accepted;
            public string Port { get; private set; }
            public async Task StartAsync()
            {
                accepted = new TaskCompletionSource<StreamSocket>();
                listener = new StreamSocketListener();
                listener.ConnectionReceived += Connected;
                await listener.BindServiceNameAsync(string.Empty);
                Port = listener.Information.LocalPort;
            }
            private void Connected(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
            {
                if (!accepted.TrySetResult(args.Socket)) args.Socket.Dispose();
                Dispose();
            }
            public async Task<StreamSocket> AcceptAsync()
            {
                if (await Task.WhenAny(accepted.Task, Task.Delay(15000)) != accepted.Task) throw new TimeoutException();
                return await accepted.Task;
            }
            public void Dispose()
            {
                if (listener == null) return;
                listener.ConnectionReceived -= Connected; listener.Dispose(); listener = null;
            }
        }

        private sealed class ParentTarget { public StorageFolder Folder; public string Name; }

        private static async Task EnsureRootsAsync()
        {
            foreach (var name in new[] { "Covers", "Config", "Metadata", "Cache" })
                await ApplicationData.Current.LocalFolder.CreateFolderAsync(name, CreationCollisionOption.OpenIfExists);
        }

        private static async Task<Dictionary<string, StorageFolder>> Roots()
        {
            await EnsureRootsAsync();
            var roots = new Dictionary<string, StorageFolder>(StringComparer.OrdinalIgnoreCase)
            {
                ["covers"] = await ApplicationData.Current.LocalFolder.GetFolderAsync("Covers"),
                ["config"] = await ApplicationData.Current.LocalFolder.GetFolderAsync("Config"),
                ["metadata"] = await ApplicationData.Current.LocalFolder.GetFolderAsync("Metadata"),
                ["cache"] = ApplicationData.Current.LocalCacheFolder
            };
            var manager = new ExternalStorageManager();
            foreach (var drive in await manager.DiscoverAndPrepareAsync())
                roots[drive.VirtualName] = drive.NxeRoot;
            return roots;
        }

        private async Task<IStorageItem> Item(string path)
        {
            if (path == "/") return null;
            var parts = Parts(path); var roots = await Roots();
            if (parts.Count == 0 || !roots.TryGetValue(parts[0], out var folder)) return null;
            IStorageItem item = folder;
            for (var i = 1; i < parts.Count; i++)
            {
                if (!(item is StorageFolder current)) return null;
                item = await current.TryGetItemAsync(parts[i]); if (item == null) return null;
            }
            return item;
        }
        private async Task<StorageFolder> Folder(string path) => await Item(path) as StorageFolder;
        private async Task<ParentTarget> Parent(string path)
        {
            if (path == "/") return null;
            var folder = await Folder(ParentPath(path)); var name = FileName(path);
            return folder == null || string.IsNullOrWhiteSpace(name) ? null : new ParentTarget { Folder = folder, Name = name };
        }

        private async Task<IReadOnlyList<string>> Listing(string path, bool names, bool machine)
        {
            if (path == "/")
            {
                var roots = await Roots();
                return roots.Keys.OrderBy(x => x).Select(x => names ? x : machine ?
                    "type=dir;modify=19700101000000; " + x : "drwxr-xr-x 1 nxe nxe 0 Jan 01 1970 " + x).ToList();
            }
            var item = await Item(path); if (item == null) return null;
            var all = new List<IStorageItem>();
            if (item is StorageFolder folder) all.AddRange(await folder.GetItemsAsync()); else all.Add(item);
            var lines = new List<string>();
            foreach (var child in all.OrderBy(x => x.Name))
            {
                if (names) { lines.Add(Safe(child.Name)); continue; }
                var directory = child.IsOfType(StorageItemTypes.Folder); ulong size = 0;
                var modified = child.DateCreated.UtcDateTime;
                if (child is StorageFile file)
                { var properties = await file.GetBasicPropertiesAsync(); size = properties.Size; modified = properties.DateModified.UtcDateTime; }
                lines.Add(machine ? "type=" + (directory ? "dir" : "file") + ";size=" + size + ";modify=" +
                    modified.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "; " + Safe(child.Name) :
                    (directory ? "d" : "-") + "rw-r--r-- 1 nxe nxe " + size + " " +
                    modified.ToString("MMM dd yyyy", CultureInfo.InvariantCulture) + " " + Safe(child.Name));
            }
            return lines;
        }

        internal static string Normalize(string cwd, string requested)
        {
            return NxeStorageContract.NormalizeVirtualPath(cwd, requested);
        }
        private static List<string> Parts(string path) => path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        private static string ParentPath(string path) { var i = path.LastIndexOf('/'); return i <= 0 ? "/" : path.Substring(0, i); }
        private static string FileName(string path) { var i = path.LastIndexOf('/'); return i < 0 ? path : path.Substring(i + 1); }
        private static string StripOptions(string arg) => string.IsNullOrWhiteSpace(arg) ? string.Empty :
            string.Join(" ", arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(x => !x.StartsWith("-")));
        private static string Safe(string name) => (name ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);

        private static async Task<string> ReadLine(DataReader reader)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (await reader.LoadAsync(1) == 0) return line.Length == 0 ? null : line.ToString();
                var value = (char)reader.ReadByte(); if (value == '\n') return line.ToString().TrimEnd('\r');
                if (line.Length < 2048) line.Append(value);
            }
        }
        private static async Task Reply(DataWriter writer, string text)
        { writer.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8; writer.WriteString(text + "\r\n"); await writer.StoreAsync(); }
        private void Activity(string text, bool storageChanged)
        { LastActivity = text; NotifyState(); if (storageChanged) StorageChanged?.Invoke(this, EventArgs.Empty); }
        private void NotifyState() => StateChanged?.Invoke(this, EventArgs.Empty);
        public static IReadOnlyList<string> Addresses()
        {
            var result = new List<string>();
            foreach (var host in NetworkInformation.GetHostNames())
                if (host.IPInformation != null && host.Type == HostNameType.Ipv4 && !result.Contains(host.CanonicalName)) result.Add(host.CanonicalName);
            return result;
        }
        public void Dispose() { Stop(); }
    }
}
