using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using NxeDashboard.Shared;
using Windows.Data.Json;
using Windows.Networking.Sockets;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Security.Cryptography;

namespace NxeDashboard.Runtime
{
    public sealed class NxeWebManagementServer : IDisposable
    {
        private const string ServicePort = "2123";
        private readonly object sync = new object();
        private readonly Func<string> controlToken;
        private readonly Func<string> pairId;
        private readonly Func<bool> ftpRunning;
        private readonly Func<string> ftpFailure;
        private readonly Func<Task> ftpStart;
        private readonly Func<Task> ftpStop;
        private readonly Func<Task> ftpRestart;
        private readonly Func<string, string, Task> ftpConfigure;
        private readonly Func<string> ftpUsername;
        private readonly Func<Task<string>> ftpPassword;
        private StreamSocketListener listener;
        private bool running;

        public bool IsRunning { get { lock (sync) return running; } }
        public string Port { get; private set; } = ServicePort;
        public event EventHandler StateChanged;

        public NxeWebManagementServer(Func<string> controlToken, Func<string> pairId, Func<bool> ftpRunning,
            Func<string> ftpFailure, Func<Task> ftpStart, Func<Task> ftpStop, Func<Task> ftpRestart,
            Func<string, string, Task> ftpConfigure = null, Func<string> ftpUsername = null,
            Func<Task<string>> ftpPassword = null)
        {
            this.controlToken = controlToken ?? throw new ArgumentNullException(nameof(controlToken));
            this.pairId = pairId ?? (() => string.Empty);
            this.ftpRunning = ftpRunning ?? throw new ArgumentNullException(nameof(ftpRunning));
            this.ftpFailure = ftpFailure ?? (() => string.Empty);
            this.ftpStart = ftpStart ?? throw new ArgumentNullException(nameof(ftpStart));
            this.ftpStop = ftpStop ?? throw new ArgumentNullException(nameof(ftpStop));
            this.ftpRestart = ftpRestart ?? throw new ArgumentNullException(nameof(ftpRestart));
            this.ftpConfigure = ftpConfigure;
            this.ftpUsername = ftpUsername;
            this.ftpPassword = ftpPassword;
        }

        public async Task StartAsync()
        {
            lock (sync) if (running) return;
            var next = new StreamSocketListener();
            next.ConnectionReceived += OnConnectionReceived;
            try
            {
                await next.BindServiceNameAsync(ServicePort);
                lock (sync)
                {
                    listener = next;
                    running = true;
                }
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                next.ConnectionReceived -= OnConnectionReceived;
                next.Dispose();
                throw;
            }
        }

        public void Stop()
        {
            StreamSocketListener current;
            lock (sync)
            {
                current = listener;
                listener = null;
                running = false;
            }
            if (current != null)
            {
                current.ConnectionReceived -= OnConnectionReceived;
                current.Dispose();
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private async void OnConnectionReceived(StreamSocketListener sender,
            StreamSocketListenerConnectionReceivedEventArgs args)
        {
            try
            {
                using (var socket = args.Socket)
                using (var reader = new DataReader(socket.InputStream))
                using (var writer = new DataWriter(socket.OutputStream))
                {
                    reader.InputStreamOptions = InputStreamOptions.Partial;
                    var request = await ReadRequestAsync(reader);
                    if (request == null) return;
                    await HandleAsync(request, reader, writer, socket.OutputStream);
                }
            }
            catch
            {
                // A browser disconnect during a transfer is a normal cancellation path.
            }
        }

        private async Task HandleAsync(HttpRequest request, DataReader reader, DataWriter writer, IOutputStream output)
        {
            if (request.Method == "OPTIONS")
            {
                await ReplyAsync(writer, 204, "");
                return;
            }
            // Public health/status endpoints for direct curl / browser testing on port 2123
            if ((request.Path == "/status" || request.Path == "/health" || request.Path == "/" || request.Path == "/api/status") && request.Method == "GET")
            {
                var health = await HealthJsonAsync();
                await ReplyAsync(writer, 200, health.Stringify());
                return;
            }
            // Public identify endpoint — returns pairId only, no auth required.
            // The website uses this to find the cloud pair record and retrieve the
            // locally cached controlToken, then re-authenticates all subsequent requests.
            if (request.Path == "/api/v1/device/identify" && request.Method == "GET")
            {
                var pid = pairId() ?? string.Empty;
                var token = controlToken() ?? string.Empty;
                var id = new JsonObject
                {
                    ["ok"]           = JsonValue.CreateBooleanValue(true),
                    ["pairId"]       = JsonValue.CreateStringValue(pid),
                    ["controlToken"] = JsonValue.CreateStringValue(token),
                    ["ready"]        = JsonValue.CreateBooleanValue(token.Length > 0 && pid.Length > 0),
                };
                await ReplyAsync(writer, 200, id.Stringify());
                return;
            }
            if (!Authorized(request))
            {
                await ReplyAsync(writer, 401, "{\"ok\":false,\"message\":\"Pairing required.\"}");
                return;
            }
            try
            {
                await HandleAuthorizedAsync(request, reader, writer, output);
            }
            catch (ArgumentException exception)
            {
                await ReplyAsync(writer, 400, JsonError(exception.Message));
            }
            catch (InvalidOperationException exception)
            {
                await ReplyAsync(writer, 400, JsonError(exception.Message));
            }
            catch (Exception exception)
            {
                await ReplyAsync(writer, 500, JsonError(exception.Message));
            }
        }

        private async Task HandleAuthorizedAsync(HttpRequest request, DataReader reader, DataWriter writer, IOutputStream output)
        {
            var path = request.Path;
            if (path == "/api/v1/device/status" && request.Method == "GET")
            {
                var status = await StatusJsonAsync();
                await ReplyAsync(writer, 200, status.Stringify());
                return;
            }
            if (path == "/api/v1/ftp/credentials" && request.Method == "GET")
            {
                var user = ftpUsername != null ? (ftpUsername() ?? string.Empty) : string.Empty;
                var pass = ftpPassword != null ? (await ftpPassword() ?? string.Empty) : string.Empty;
                var creds = new JsonObject
                {
                    ["ok"] = JsonValue.CreateBooleanValue(true),
                    ["hasCredentials"] = JsonValue.CreateBooleanValue(!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(pass)),
                    ["username"] = JsonValue.CreateStringValue(user),
                    ["password"] = JsonValue.CreateStringValue(pass)
                };
                await ReplyAsync(writer, 200, creds.Stringify());
                return;
            }
            if (path == "/api/v1/ftp/credentials" && request.Method == "POST")
            {
                var body = await ReadBodyAsync(reader, request.ContentLength);
                var json = Parse(body);
                var user = json.ContainsKey("username") ? json.GetNamedString("username", "") : "";
                var pass = json.ContainsKey("password") ? json.GetNamedString("password", "") : "";
                if (ftpConfigure != null)
                {
                    await ftpConfigure(user, pass);
                }
                var status = await StatusJsonAsync();
                await ReplyAsync(writer, 200, status.Stringify());
                return;
            }
            if (path == "/api/v1/files" && request.Method == "GET")
            {
                await ReplyAsync(writer, 200, await ListJsonAsync(request.Query("path") ?? "/"));
                return;
            }
            if (path == "/api/v1/files/download" && request.Method == "GET")
            {
                await DownloadAsync(writer, output, request.Query("path"));
                return;
            }
            if (path == "/api/v1/files/upload" && request.Method == "POST")
            {
                await UploadAsync(request, reader, writer);
                return;
            }
            if (path == "/api/v1/files/folders" && request.Method == "POST")
            {
                var body = await ReadBodyAsync(reader, request.ContentLength);
                await CreateFolderAsync(writer, body);
                return;
            }
            if (path == "/api/v1/files/rename" && request.Method == "POST")
            {
                var body = await ReadBodyAsync(reader, request.ContentLength);
                await RenameAsync(writer, body);
                return;
            }
            if (path == "/api/v1/files/move" && request.Method == "POST")
            {
                var body = await ReadBodyAsync(reader, request.ContentLength);
                await MoveAsync(writer, body);
                return;
            }
            if (path == "/api/v1/files" && request.Method == "DELETE")
            {
                var body = await ReadBodyAsync(reader, request.ContentLength);
                await DeleteAsync(writer, body);
                return;
            }
            if (path == "/api/v1/ftp/start" && request.Method == "POST")
            {
                await ftpStart();
                var status = await StatusJsonAsync();
                await ReplyAsync(writer, 200, status.Stringify());
                return;
            }
            if (path == "/api/v1/ftp/stop" && request.Method == "POST")
            {
                await ftpStop();
                var status = await StatusJsonAsync();
                await ReplyAsync(writer, 200, status.Stringify());
                return;
            }
            if (path == "/api/v1/ftp/restart" && request.Method == "POST")
            {
                await ftpRestart();
                var status = await StatusJsonAsync();
                await ReplyAsync(writer, 200, status.Stringify());
                return;
            }
            await ReplyAsync(writer, 404, "{\"ok\":false,\"message\":\"Not found.\"}");
        }

        private bool Authorized(HttpRequest request)
        {
            var expected = controlToken() ?? string.Empty;
            if (expected.Length == 0) return false;
            var supplied = request.Header("X-NXE-Control");
            if (string.IsNullOrEmpty(supplied))
            {
                var bearer = request.Header("Authorization");
                supplied = bearer != null && bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? bearer.Substring(7).Trim() : string.Empty;
            }
            return string.Equals(expected, supplied, StringComparison.Ordinal);
        }

        private static string JsonError(string message)
        {
            var json = new JsonObject
            {
                ["ok"] = JsonValue.CreateBooleanValue(false),
                ["message"] = JsonValue.CreateStringValue(message ?? "Request failed."),
            };
            return json.Stringify();
        }

        private async Task<JsonObject> HealthJsonAsync()
        {
            var isRunning = ftpRunning();
            var storageVerified = false;
            try
            {
                var manager = new ExternalStorageManager();
                var drives = await manager.DiscoverAndPrepareAsync();
                if (drives != null && drives.Count > 0 && drives[0].NxeRoot != null)
                {
                    storageVerified = true;
                }
            }
            catch
            {
                storageVerified = false;
            }

            return new JsonObject
            {
                ["ok"] = JsonValue.CreateBooleanValue(true),
                ["service"] = JsonValue.CreateStringValue("NXE"),
                ["controlServer"] = JsonValue.CreateStringValue(running ? "running" : "stopped"),
                ["ftpServer"] = JsonValue.CreateStringValue(isRunning ? "running" : "stopped"),
                ["ftpPort"] = JsonValue.CreateNumberValue(2121),
                ["webPort"] = JsonValue.CreateNumberValue(2123),
                ["storageReadable"] = JsonValue.CreateBooleanValue(storageVerified),
                ["version"] = JsonValue.CreateStringValue("1.0.0")
            };
        }

        private async Task<JsonObject> StatusJsonAsync()
        {
            var storageVerified = false;
            var storageMessage = string.Empty;
            try
            {
                var manager = new ExternalStorageManager();
                var drives = await manager.DiscoverAndPrepareAsync();
                if (drives != null && drives.Count > 0)
                {
                    var primary = drives[0];
                    if (primary.NxeRoot != null)
                    {
                        await primary.NxeRoot.GetItemsAsync(0, 1);
                        storageVerified = true;
                        storageMessage = primary.VirtualName + " (" + primary.DriveRoot.Name + ")";
                    }
                }
                else
                {
                    storageMessage = "No external storage connected";
                }
            }
            catch (Exception ex)
            {
                storageVerified = false;
                storageMessage = ex.Message;
            }

            var username = ftpUsername != null ? (ftpUsername() ?? string.Empty) : string.Empty;
            return new JsonObject
            {
                ["ok"] = JsonValue.CreateBooleanValue(true),
                ["ftpRunning"] = JsonValue.CreateBooleanValue(ftpRunning()),
                ["ftpPort"] = JsonValue.CreateStringValue("2121"),
                ["webPort"] = JsonValue.CreateStringValue(ServicePort),
                ["failure"] = JsonValue.CreateStringValue(ftpFailure() ?? string.Empty),
                ["storageVerified"] = JsonValue.CreateBooleanValue(storageVerified),
                ["storageMessage"] = JsonValue.CreateStringValue(storageMessage),
                ["hasCredentials"] = JsonValue.CreateBooleanValue(!string.IsNullOrEmpty(username)),
                ["username"] = JsonValue.CreateStringValue(username),
            };
        }

        private async Task<string> ListJsonAsync(string requested)
        {
            var path = Normalize(requested);
            var array = new JsonArray();
            if (path == "/")
            {
                foreach (var root in await RootsAsync())
                    array.Add(ItemJson(root.Key, true, 0, DateTimeOffset.MinValue));
            }
            else
            {
                var folder = await FolderAsync(path);
                if (folder == null) throw new InvalidOperationException("Directory not found.");
                foreach (var item in await folder.GetItemsAsync())
                {
                    ulong size = 0;
                    var modified = item.DateCreated;
                    var isFolder = item.IsOfType(StorageItemTypes.Folder);
                    if (item is StorageFile file)
                    {
                        var properties = await file.GetBasicPropertiesAsync();
                        size = properties.Size;
                        modified = properties.DateModified;
                    }
                    array.Add(ItemJson(item.Name, isFolder, size, modified));
                }
            }
            var result = new JsonObject
            {
                ["ok"] = JsonValue.CreateBooleanValue(true),
                ["path"] = JsonValue.CreateStringValue(path),
                ["items"] = array,
            };
            return result.Stringify();
        }

        private static JsonObject ItemJson(string name, bool folder, ulong size, DateTimeOffset modified)
        {
            return new JsonObject
            {
                ["name"] = JsonValue.CreateStringValue(name),
                ["type"] = JsonValue.CreateStringValue(folder ? "folder" : "file"),
                ["size"] = JsonValue.CreateNumberValue((double)size),
                ["modified"] = JsonValue.CreateStringValue(modified == DateTimeOffset.MinValue ? "" : modified.ToString("o")),
            };
        }

        private async Task UploadAsync(HttpRequest request, DataReader reader, DataWriter writer)
        {
            var path = Normalize(request.Header("X-NXE-Path") ?? request.Query("path") ?? "/");
            var name = SafeName(request.Header("X-NXE-File-Name") ?? request.Query("name"));
            if (path == "/" || string.IsNullOrEmpty(name) || request.ContentLength < 0)
                throw new InvalidOperationException("Upload path, filename, and length are required.");
            var parent = await FolderAsync(path);
            if (parent == null) throw new InvalidOperationException("Upload directory not found.");
            var partName = name + ".part";
            var part = await parent.CreateFileAsync(partName, CreationCollisionOption.ReplaceExisting);
            using (var stream = await part.OpenAsync(FileAccessMode.ReadWrite))
            using (var output = stream.GetOutputStreamAt(0))
            {
                var remaining = request.ContentLength;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(64 * 1024, remaining);
                    if (await reader.LoadAsync((uint)count) == 0) throw new InvalidOperationException("Upload ended early.");
                    var bytes = new byte[count];
                    reader.ReadBytes(bytes);
                    await output.WriteAsync(bytes.AsBuffer());
                    remaining -= count;
                }
                await output.FlushAsync();
            }
            var existing = await parent.TryGetItemAsync(name);
            if (existing != null) await existing.DeleteAsync(StorageDeleteOption.PermanentDelete);
            await part.RenameAsync(name, NameCollisionOption.ReplaceExisting);
            await ReplyAsync(writer, 200, "{\"ok\":true}");
        }

        private async Task DownloadAsync(DataWriter writer, IOutputStream output, string requested)
        {
            var file = await ItemAsync(Normalize(requested)) as StorageFile;
            if (file == null) { await ReplyAsync(writer, 404, "{\"ok\":false,\"message\":\"File not found.\"}"); return; }
            var properties = await file.GetBasicPropertiesAsync();
            await WriteHeadersAsync(writer, 200, "application/octet-stream", (long)properties.Size, false);
            using (var stream = await file.OpenReadAsync())
                await RandomAccessStream.CopyAsync(stream, output);
        }

        private async Task CreateFolderAsync(DataWriter writer, string body)
        {
            var json = Parse(body);
            var path = Normalize(json.GetNamedString("path", "/"));
            var name = SafeName(json.GetNamedString("name", ""));
            var parent = await FolderAsync(path);
            if (parent == null || name == null) throw new InvalidOperationException("Folder target is invalid.");
            await parent.CreateFolderAsync(name, CreationCollisionOption.FailIfExists);
            await ReplyAsync(writer, 200, "{\"ok\":true}");
        }

        private async Task RenameAsync(DataWriter writer, string body)
        {
            var json = Parse(body);
            var item = await ItemAsync(Normalize(json.GetNamedString("path", "")));
            var name = SafeName(json.GetNamedString("name", ""));
            if (item == null || name == null) throw new InvalidOperationException("Rename target is invalid.");
            await item.RenameAsync(name, NameCollisionOption.FailIfExists);
            await ReplyAsync(writer, 200, "{\"ok\":true}");
        }

        private async Task MoveAsync(DataWriter writer, string body)
        {
            var json = Parse(body);
            var item = await ItemAsync(Normalize(json.GetNamedString("path", "")));
            var destination = await FolderAsync(Normalize(json.GetNamedString("destination", "/")));
            if (item == null || destination == null) throw new InvalidOperationException("Move target is invalid.");
            if (item is StorageFile file) await file.MoveAsync(destination, file.Name, NameCollisionOption.FailIfExists);
            else if (item is StorageFolder folder) await Task.Run(() => System.IO.Directory.Move(folder.Path, System.IO.Path.Combine(destination.Path, folder.Name)));
            await ReplyAsync(writer, 200, "{\"ok\":true}");
        }

        private async Task DeleteAsync(DataWriter writer, string body)
        {
            var json = Parse(body);
            var path = Normalize(json.GetNamedString("path", ""));
            if (path == "/") throw new InvalidOperationException("The virtual root cannot be deleted.");
            var item = await ItemAsync(path);
            if (item == null) throw new InvalidOperationException("Delete target not found.");
            await item.DeleteAsync(StorageDeleteOption.PermanentDelete);
            await ReplyAsync(writer, 200, "{\"ok\":true}");
        }

        public async Task<JsonObject> ExecuteRelayRequestAsync(string method, string rawPath, string body)
        {
            method = (method ?? "GET").ToUpperInvariant();
            var request = new HttpRequest { Method = method, RawPath = rawPath ?? "/" };
            var path = request.Path;
            if (path == "/api/v1/device/status" && method == "GET") return await StatusJsonAsync();
            if (path == "/api/v1/ftp/credentials" && method == "GET")
            {
                var user = ftpUsername != null ? (ftpUsername() ?? string.Empty) : string.Empty;
                var pass = ftpPassword != null ? (await ftpPassword() ?? string.Empty) : string.Empty;
                return new JsonObject
                {
                    ["ok"] = JsonValue.CreateBooleanValue(true),
                    ["hasCredentials"] = JsonValue.CreateBooleanValue(!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(pass)),
                    ["username"] = JsonValue.CreateStringValue(user),
                    ["password"] = JsonValue.CreateStringValue(pass)
                };
            }
            if (path == "/api/v1/ftp/credentials" && method == "POST")
            {
                var json = Parse(body);
                var user = json.ContainsKey("username") ? json.GetNamedString("username", "") : "";
                var pass = json.ContainsKey("password") ? json.GetNamedString("password", "") : "";
                if (ftpConfigure != null)
                {
                    await ftpConfigure(user, pass);
                }
                return await StatusJsonAsync();
            }
            if (path == "/api/v1/files" && method == "GET")
            {
                JsonObject listing;
                if (!JsonObject.TryParse(await ListJsonAsync(request.Query("path") ?? "/"), out listing))
                    throw new InvalidOperationException("NXE returned an invalid directory listing.");
                return listing;
            }
            if (path == "/api/v1/files/folders" && method == "POST")
            {
                var json = Parse(body);
                var parent = await FolderAsync(Normalize(json.GetNamedString("path", "/")));
                var name = SafeName(json.GetNamedString("name", ""));
                if (parent == null || name == null) throw new InvalidOperationException("Folder target is invalid.");
                await parent.CreateFolderAsync(name, CreationCollisionOption.FailIfExists);
                return OkJson();
            }
            if (path == "/api/v1/files/rename" && method == "POST")
            {
                var json = Parse(body);
                var item = await ItemAsync(Normalize(json.GetNamedString("path", "")));
                var name = SafeName(json.GetNamedString("name", ""));
                if (item == null || name == null) throw new InvalidOperationException("Rename target is invalid.");
                await item.RenameAsync(name, NameCollisionOption.FailIfExists);
                return OkJson();
            }
            if (path == "/api/v1/files/move" && method == "POST")
            {
                var json = Parse(body);
                var item = await ItemAsync(Normalize(json.GetNamedString("path", "")));
                var destination = await FolderAsync(Normalize(json.GetNamedString("destination", "/")));
                if (item == null || destination == null) throw new InvalidOperationException("Move target is invalid.");
                if (item is StorageFile file) await file.MoveAsync(destination, file.Name, NameCollisionOption.FailIfExists);
                else if (item is StorageFolder folder) await Task.Run(() => System.IO.Directory.Move(folder.Path, System.IO.Path.Combine(destination.Path, folder.Name)));
                return OkJson();
            }
            if (path == "/api/v1/files" && method == "DELETE")
            {
                var json = Parse(body);
                var target = Normalize(json.GetNamedString("path", ""));
                if (target == "/") throw new InvalidOperationException("The virtual root cannot be deleted.");
                var item = await ItemAsync(target);
                if (item == null) throw new InvalidOperationException("Delete target not found.");
                await item.DeleteAsync(StorageDeleteOption.PermanentDelete);
                return OkJson();
            }
            if (path == "/api/v1/ftp/start" && method == "POST") await ftpStart();
            else if (path == "/api/v1/ftp/stop" && method == "POST") await ftpStop();
            else if (path == "/api/v1/ftp/restart" && method == "POST") await ftpRestart();
            else throw new InvalidOperationException("NXE relay route was not found.");
            return await StatusJsonAsync();
        }

        public async Task UploadRelayBase64Async(string folderPath, string name, string base64)
        {
            var path = Normalize(folderPath ?? "/");
            name = SafeName(name);
            if (path == "/" || name == null) throw new InvalidOperationException("Upload path and filename are required.");
            if (string.IsNullOrEmpty(base64) || base64.Length > 28 * 1024 * 1024)
                throw new InvalidOperationException("Cloud relay uploads are limited to 20 MB per file.");
            var parent = await FolderAsync(path);
            if (parent == null) throw new InvalidOperationException("Upload directory not found.");
            var part = await parent.CreateFileAsync(name + ".part", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBufferAsync(part, CryptographicBuffer.DecodeFromBase64String(base64));
            var existing = await parent.TryGetItemAsync(name);
            if (existing != null) await existing.DeleteAsync(StorageDeleteOption.PermanentDelete);
            await part.RenameAsync(name, NameCollisionOption.ReplaceExisting);
        }

        public async Task<string> DownloadRelayBase64Async(string requested)
        {
            var file = await ItemAsync(Normalize(requested)) as StorageFile;
            if (file == null) throw new InvalidOperationException("File not found.");
            var properties = await file.GetBasicPropertiesAsync();
            if (properties.Size > 20UL * 1024UL * 1024UL)
                throw new InvalidOperationException("Cloud relay downloads are limited to 20 MB per file.");
            return CryptographicBuffer.EncodeToBase64String(await FileIO.ReadBufferAsync(file));
        }

        private static JsonObject OkJson()
        {
            return new JsonObject { ["ok"] = JsonValue.CreateBooleanValue(true) };
        }

        private static JsonObject Parse(string body)
        {
            JsonObject json;
            if (!JsonObject.TryParse(body ?? "", out json)) throw new InvalidOperationException("Invalid JSON request.");
            return json;
        }

        private static string SafeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "." || value == ".." || value.IndexOfAny(new[] { '/', '\\', '\0', '\r', '\n' }) >= 0)
                return null;
            return value.Trim();
        }

        private static string Normalize(string path)
        {
            return NxeStorageContract.NormalizeVirtualPath("/", path ?? "/");
        }

        private async Task<IStorageItem> ItemAsync(string path)
        {
            if (path == "/") return null;
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var roots = await RootsAsync();
            StorageFolder root;
            if (!roots.TryGetValue(parts[0], out root)) return null;
            IStorageItem item = root;
            for (var index = 1; index < parts.Length; index++)
            {
                var folder = item as StorageFolder;
                if (folder == null) return null;
                item = await folder.TryGetItemAsync(parts[index]);
                if (item == null) return null;
            }
            return item;
        }

        private async Task<StorageFolder> FolderAsync(string path) => await ItemAsync(path) as StorageFolder;

        private static async Task<Dictionary<string, StorageFolder>> RootsAsync()
        {
            var roots = new Dictionary<string, StorageFolder>(StringComparer.OrdinalIgnoreCase)
            {
                ["covers"] = await ApplicationData.Current.LocalFolder.GetFolderAsync("Covers"),
                ["config"] = await ApplicationData.Current.LocalFolder.GetFolderAsync("Config"),
                ["metadata"] = await ApplicationData.Current.LocalFolder.GetFolderAsync("Metadata"),
                ["cache"] = ApplicationData.Current.LocalCacheFolder,
            };
            foreach (var drive in await new ExternalStorageManager().DiscoverAndPrepareAsync()) roots[drive.VirtualName] = drive.NxeRoot;
            return roots;
        }

        private static async Task<HttpRequest> ReadRequestAsync(DataReader reader)
        {
            var bytes = new List<byte>();
            while (bytes.Count < 32768)
            {
                var loaded = await reader.LoadAsync(4096);
                if (loaded == 0) break;
                var chunk = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(chunk);
                bytes.AddRange(chunk);

                var count = bytes.Count;
                var foundEnd = false;
                for (int i = 3; i < count; i++)
                {
                    if (bytes[i - 3] == 13 && bytes[i - 2] == 10 && bytes[i - 1] == 13 && bytes[i] == 10)
                    {
                        foundEnd = true;
                        break;
                    }
                }
                if (foundEnd) break;
            }
            if (bytes.Count == 0) return null;
            var text = Encoding.UTF8.GetString(bytes.ToArray(), 0, bytes.Count);
            var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) return null;
            var first = lines[0].Split(new[] { ' ' }, 3);
            if (first.Length < 2) return null;
            var request = new HttpRequest { Method = first[0].ToUpperInvariant(), RawPath = first.Length > 1 ? first[1] : "/" };
            for (var index = 1; index < lines.Length; index++)
            {
                var separator = lines[index].IndexOf(':');
                if (separator <= 0) continue;
                request.Headers[lines[index].Substring(0, separator).Trim()] = lines[index].Substring(separator + 1).Trim();
            }
            long contentLength;
            request.ContentLength = long.TryParse(request.Header("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) ? contentLength : 0;
            return request;
        }

        private static async Task<string> ReadBodyAsync(DataReader reader, long length)
        {
            if (length <= 0) return string.Empty;
            if (length > 1024 * 1024) throw new InvalidOperationException("Request body is too large.");
            var bytes = new byte[(int)length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = (int)Math.Min(64 * 1024, bytes.Length - offset);
                if (await reader.LoadAsync((uint)count) == 0) throw new InvalidOperationException("Request body ended early.");
                var chunk = new byte[count];
                reader.ReadBytes(chunk);
                System.Buffer.BlockCopy(chunk, 0, bytes, offset, chunk.Length);
                offset += chunk.Length;
            }
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        private static async Task ReplyAsync(DataWriter writer, int status, string body)
        {
            await WriteHeadersAsync(writer, status, "application/json; charset=utf-8", Encoding.UTF8.GetByteCount(body), true);
            writer.WriteString(body);
            await writer.StoreAsync();
        }

        private static async Task WriteHeadersAsync(DataWriter writer, int status, string contentType, long length, bool json)
        {
            var reason = status == 200 ? "OK" : status == 204 ? "No Content" : status == 401 ? "Unauthorized" : status == 404 ? "Not Found" : "Bad Request";
            writer.WriteString("HTTP/1.1 " + status + " " + reason + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Private-Network: true\r\n" +
                "Access-Control-Allow-Headers: Content-Type, X-NXE-Control, Authorization, X-NXE-Path, X-NXE-File-Name\r\n" +
                "Access-Control-Allow-Methods: GET, POST, DELETE, OPTIONS\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n");
            await writer.StoreAsync();
        }

        public void Dispose() { Stop(); }

        private sealed class HttpRequest
        {
            public string Method;
            public string RawPath;
            public long ContentLength;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Path { get { var index = RawPath.IndexOf('?'); return index < 0 ? RawPath : RawPath.Substring(0, index); } }
            public string Header(string name) { string value; return Headers.TryGetValue(name, out value) ? value : null; }
            public string Query(string name)
            {
                var index = RawPath.IndexOf('?');
                if (index < 0) return null;
                foreach (var part in RawPath.Substring(index + 1).Split('&'))
                {
                    var pair = part.Split(new[] { '=' }, 2);
                    if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.OrdinalIgnoreCase)) return Uri.UnescapeDataString(pair[1]);
                }
                return null;
            }
        }
    }

}

