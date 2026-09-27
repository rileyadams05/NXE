using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Networking.Connectivity;
using Windows.Storage.Streams;

namespace NxeDashboard.Runtime
{
    public sealed class FtpTransferService : IDisposable
    {
        private readonly object syncRoot = new object();
        private readonly HashSet<StreamSocket> clients = new HashSet<StreamSocket>();
        private StreamSocketListener listener;

        public bool IsRunning { get; private set; }
        public string Port { get; private set; }
        public string Username { get; private set; }
        private string password;
        public int ActiveConnections
        {
            get { lock (syncRoot) return clients.Count; }
        }

        public event EventHandler StateChanged;

        public async Task StartAsync(string port, string username, string suppliedPassword)
        {
            if (IsRunning) return;
            listener = new StreamSocketListener();
            listener.ConnectionReceived += OnConnectionReceived;
            try
            {
                await listener.BindServiceNameAsync(port);
                Port = port;
                Username = username ?? string.Empty;
                password = suppliedPassword ?? string.Empty;
                IsRunning = true;
                RaiseStateChanged();
            }
            catch
            {
                listener.ConnectionReceived -= OnConnectionReceived;
                listener.Dispose();
                listener = null;
                throw;
            }
        }

        public void Stop()
        {
            if (listener != null)
            {
                listener.ConnectionReceived -= OnConnectionReceived;
                listener.Dispose();
                listener = null;
            }

            lock (syncRoot)
            {
                foreach (var client in clients) client.Dispose();
                clients.Clear();
            }
            IsRunning = false;
            Port = null;
            Username = null;
            password = null;
            RaiseStateChanged();
        }

        private async void OnConnectionReceived(
            StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            var socket = args.Socket;
            lock (syncRoot) clients.Add(socket);
            RaiseStateChanged();
            try
            {
                using (var reader = new DataReader(socket.InputStream))
                using (var writer = new DataWriter(socket.OutputStream))
                {
                    reader.InputStreamOptions = InputStreamOptions.Partial;
                    await WriteReplyAsync(writer, "220 NXE FTP ready");
                    var authenticationRequired = !string.IsNullOrEmpty(Username) ||
                        !string.IsNullOrEmpty(password);
                    var authenticated = !authenticationRequired;
                    var acceptedUser = !authenticationRequired;
                    while (IsRunning)
                    {
                        var line = await ReadLineAsync(reader);
                        if (line == null) break;
                        var separator = line.IndexOf(' ');
                        var command = (separator < 0 ? line : line.Substring(0, separator))
                            .Trim().ToUpperInvariant();
                        switch (command)
                        {
                            case "USER":
                                var requestedUser = separator < 0
                                    ? string.Empty : line.Substring(separator + 1).Trim();
                                acceptedUser = !authenticationRequired ||
                                    string.Equals(requestedUser, Username, StringComparison.Ordinal);
                                await WriteReplyAsync(writer, acceptedUser
                                    ? (authenticationRequired ? "331 Password required" : "230 Logged in")
                                    : "530 Invalid username");
                                break;
                            case "PASS":
                                var requestedPassword = separator < 0
                                    ? string.Empty : line.Substring(separator + 1);
                                authenticated = acceptedUser && (!authenticationRequired ||
                                    string.Equals(requestedPassword, password, StringComparison.Ordinal));
                                await WriteReplyAsync(writer,
                                    authenticated ? "230 Logged in" : "530 Login incorrect");
                                break;
                            case "SYST":
                                await WriteReplyAsync(writer, "215 NXE UWP");
                                break;
                            case "TYPE":
                                await WriteReplyAsync(writer, "200 Type set");
                                break;
                            case "NOOP":
                                await WriteReplyAsync(writer, "200 OK");
                                break;
                            case "PWD":
                                await WriteReplyAsync(writer, authenticated
                                    ? "257 \"/\" is the current directory"
                                    : "530 Not logged in");
                                break;
                            case "FEAT":
                                await WriteReplyAsync(writer, "211 No data-transfer extensions enabled");
                                break;
                            case "QUIT":
                                await WriteReplyAsync(writer, "221 Goodbye");
                                return;
                            default:
                                await WriteReplyAsync(writer,
                                    authenticated ? "502 Command not implemented" : "530 Not logged in");
                                break;
                        }
                    }
                }
            }
            catch
            {
                // Disconnects and suspension are normal lifecycle events.
            }
            finally
            {
                lock (syncRoot) clients.Remove(socket);
                socket.Dispose();
                RaiseStateChanged();
            }
        }

        private static async Task<string> ReadLineAsync(DataReader reader)
        {
            var line = new StringBuilder();
            while (true)
            {
                var loaded = await reader.LoadAsync(1);
                if (loaded == 0) return line.Length == 0 ? null : line.ToString();
                var value = (char)reader.ReadByte();
                if (value == '\n') return line.ToString().TrimEnd('\r');
                if (line.Length < 2048) line.Append(value);
            }
        }

        private static async Task WriteReplyAsync(DataWriter writer, string reply)
        {
            writer.WriteString(reply + "\r\n");
            await writer.StoreAsync();
        }

        private void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public static IReadOnlyList<string> GetLocalAddresses()
        {
            var addresses = new List<string>();
            foreach (var hostName in NetworkInformation.GetHostNames())
            {
                if (hostName.IPInformation != null &&
                    hostName.Type == Windows.Networking.HostNameType.Ipv4 &&
                    !addresses.Contains(hostName.CanonicalName))
                    addresses.Add(hostName.CanonicalName);
            }
            return addresses;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
