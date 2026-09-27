using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace NxeDashboard.Runtime
{
    internal static class FtpStartupVerification
    {
        public static async Task<string> RunAsync()
        {
            var results = new List<string>();
            var address = NxeFtpServer.Addresses().FirstOrDefault() ?? "127.0.0.1";
            using (var anonymous = new NxeFtpServer())
            {
                await anonymous.StartAsync("2121", string.Empty, string.Empty);
                using (var client = await Client.ConnectAsync(address, "2121"))
                {
                    await client.ExpectAsync("USER anonymous", 230);
                    results.Add("PASS authentication disabled");
                    await client.ExpectAsync("PWD", 257);
                    await client.ExpectAsync("FEAT", 211);
                    await client.ListAsync("/");
                    results.Add("PASS PASV LIST");
                    await client.NameListAsync("/");
                    results.Add("PASS PASV NLST");
                    await client.ExpectAsync("CWD /config", 250);
                    await client.IgnoreFailureAsync("RMD ftp-self-test");
                    await client.ExpectAsync("MKD ftp-self-test", 257);
                    await client.ExpectAsync("CWD ftp-self-test", 250);
                    var small = Encoding.UTF8.GetBytes("NXE FTP self test\r\n");
                    await client.StoreAsync("small.bin", small);
                    await client.ExpectAsync("SIZE small.bin", 213);
                    await client.ExpectAsync("MDTM small.bin", 213);
                    var received = await client.RetrieveAsync("small.bin");
                    if (!HashesMatch(small, received)) throw new InvalidOperationException("Small transfer hash mismatch.");
                    results.Add("PASS STOR RETR SIZE MDTM hash");
                    await client.ExpectAsync("RNFR small.bin", 350);
                    await client.ExpectAsync("RNTO renamed.bin", 250);
                    await client.ExpectAsync("MKD moved", 257);
                    await client.ExpectAsync("RNFR renamed.bin", 350);
                    await client.ExpectAsync("RNTO moved/renamed.bin", 250);
                    await client.ExpectAsync("DELE moved/renamed.bin", 250);
                    await client.ExpectAsync("RMD moved", 250);
                    results.Add("PASS rename move delete directories");
                    await client.ExpectAsync("MKD nonempty", 257);
                    await client.ExpectAsync("CWD nonempty", 250);
                    await client.StoreAsync("keep.bin", small);
                    await client.ExpectAsync("CWD ..", 250);
                    await client.ExpectAsync("RMD nonempty", 550);
                    await client.ExpectAsync("DELE nonempty/keep.bin", 250);
                    await client.ExpectAsync("RMD nonempty", 250);
                    await client.ExpectAsync("RMD /config", 550);
                    await client.ExpectAsync("RNFR /config", 550);
                    results.Add("PASS non-empty and virtual-root deletion guards");
                    try { await client.ExpectAsync("CWD ../../..", 250); throw new InvalidOperationException("Traversal accepted."); }
                    catch (FtpExpectedException) { results.Add("PASS traversal rejected"); }

                    var large = new byte[8 * 1024 * 1024];
                    for (var i = 0; i < large.Length; i += 4096)
                        Encoding.ASCII.GetBytes("NXE-STREAM-BLOCK").CopyTo(large, i);
                    await client.StoreAsync("large.bin", large);
                    received = await client.RetrieveAsync("large.bin");
                    if (!HashesMatch(large, received)) throw new InvalidOperationException("Large transfer hash mismatch.");
                    await client.ExpectAsync("DELE large.bin", 250);
                    results.Add("PASS large streamed transfer hash");

                    var sessions = Enumerable.Range(0, 3).Select(async ignored =>
                    {
                        using (var parallel = await Client.ConnectAsync(address, "2121"))
                        {
                            await parallel.ExpectAsync("USER anonymous", 230);
                            await parallel.ExpectAsync("PWD", 257);
                        }
                    });
                    await Task.WhenAll(sessions);
                    results.Add("PASS simultaneous sessions");

                    using (var interrupted = await Client.ConnectAsync(address, "2121"))
                    {
                        await interrupted.ExpectAsync("USER anonymous", 230);
                        await interrupted.ExpectAsync("CWD /config/ftp-self-test", 250);
                        await interrupted.AbortStoreAsync("interrupted.bin", Encoding.ASCII.GetBytes("partial"));
                    }
                    await Task.Delay(300);
                    var names = await client.GetNameListAsync(".");
                    if (names.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Any(name => name.Equals("interrupted.bin", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Interrupted upload was published as a complete file.");
                    if (names.IndexOf("interrupted.bin.part", StringComparison.OrdinalIgnoreCase) >= 0)
                        await client.ExpectAsync("DELE interrupted.bin.part", 250);
                    results.Add("PASS interrupted transfer not published");

                    await client.ExpectAsync("CWD /config", 250);
                    await client.ExpectAsync("RMD ftp-self-test", 250);
                    await client.ExpectAsync("QUIT", 221);
                }
                anonymous.Stop();
            }

            using (var authenticated = new NxeFtpServer())
            {
                await authenticated.StartAsync("2122", "nxe-test", "nxe-test-password");
                using (var wrong = await Client.ConnectAsync(address, "2122"))
                {
                    await wrong.ExpectAsync("USER nxe-test", 331);
                    await wrong.ExpectAsync("PASS wrong", 530);
                    results.Add("PASS authentication rejects wrong password");
                }
                using (var valid = await Client.ConnectAsync(address, "2122"))
                {
                    await valid.ExpectAsync("USER nxe-test", 331);
                    await valid.ExpectAsync("PASS nxe-test-password", 230);
                    results.Add("PASS authentication accepts configured credentials");
                    await valid.ExpectAsync("QUIT", 221);
                }
                authenticated.Stop();
            }
            return string.Join("\r\n", results) + "\r\nResult: " + results.Count + " passed, 0 failed\r\n";
        }

        private static bool HashesMatch(byte[] first, byte[] second)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(first).SequenceEqual(sha.ComputeHash(second));
        }

        private sealed class FtpExpectedException : Exception { }

        private sealed class Client : IDisposable
        {
            private readonly string host;
            private readonly StreamSocket control;
            private readonly DataReader reader;
            private readonly DataWriter writer;

            private Client(string host, StreamSocket control)
            {
                this.host = host;
                this.control = control;
                reader = new DataReader(control.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
                writer = new DataWriter(control.OutputStream)
                { UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8 };
            }

            public static async Task<Client> ConnectAsync(string host, string port)
            {
                var socket = new StreamSocket();
                await socket.ConnectAsync(new HostName(host), port);
                var client = new Client(host, socket);
                var greeting = await client.ReadReplyAsync();
                if (greeting.Code != 220) throw new InvalidOperationException(greeting.Text);
                return client;
            }

            public async Task ExpectAsync(string command, int expected)
            {
                await SendAsync(command);
                var reply = await ReadReplyAsync();
                if (reply.Code != expected) throw new FtpExpectedException();
            }

            public async Task IgnoreFailureAsync(string command)
            {
                await SendAsync(command);
                await ReadReplyAsync();
            }

            public async Task ListAsync(string path) => await ReceiveDataAsync("LIST " + path);
            public async Task NameListAsync(string path) => await ReceiveDataAsync("NLST " + path);
            public async Task<string> GetNameListAsync(string path) => await ReceiveDataTextAsync("NLST " + path);

            public async Task StoreAsync(string name, byte[] data)
            {
                await ExpectAsync("ALLO " + data.Length, 200);
                var dataSocket = await OpenPassiveAsync();
                await SendAsync("STOR " + name);
                if ((await ReadReplyAsync()).Code != 150) throw new InvalidOperationException("STOR did not open data channel.");
                using (dataSocket)
                using (var output = new DataWriter(dataSocket.OutputStream))
                {
                    output.WriteBytes(data);
                    await output.StoreAsync();
                }
                if ((await ReadReplyAsync()).Code != 226) throw new InvalidOperationException("STOR did not complete.");
            }

            public async Task<byte[]> RetrieveAsync(string name)
            {
                var dataSocket = await OpenPassiveAsync();
                await SendAsync("RETR " + name);
                if ((await ReadReplyAsync()).Code != 150) throw new InvalidOperationException("RETR did not open data channel.");
                var bytes = new List<byte>();
                using (dataSocket)
                using (var input = new DataReader(dataSocket.InputStream))
                {
                    input.InputStreamOptions = InputStreamOptions.Partial;
                    while (await input.LoadAsync(65536) > 0)
                    {
                        var block = new byte[input.UnconsumedBufferLength];
                        input.ReadBytes(block);
                        bytes.AddRange(block);
                    }
                }
                if ((await ReadReplyAsync()).Code != 226) throw new InvalidOperationException("RETR did not complete.");
                return bytes.ToArray();
            }

            private async Task ReceiveDataAsync(string command)
            {
                await ReceiveDataTextAsync(command);
            }

            private async Task<string> ReceiveDataTextAsync(string command)
            {
                var dataSocket = await OpenPassiveAsync();
                await SendAsync(command);
                if ((await ReadReplyAsync()).Code != 150) throw new InvalidOperationException(command + " failed.");
                var text = new StringBuilder();
                using (dataSocket)
                using (var input = new DataReader(dataSocket.InputStream))
                {
                    input.InputStreamOptions = InputStreamOptions.Partial;
                    while (await input.LoadAsync(4096) > 0)
                        text.Append(input.ReadString(input.UnconsumedBufferLength));
                }
                if ((await ReadReplyAsync()).Code != 226) throw new InvalidOperationException(command + " did not complete.");
                return text.ToString();
            }

            public async Task AbortStoreAsync(string name, byte[] partialData)
            {
                await ExpectAsync("ALLO " + (partialData.Length + 1024), 200);
                var dataSocket = await OpenPassiveAsync();
                await SendAsync("STOR " + name);
                if ((await ReadReplyAsync()).Code != 150) throw new InvalidOperationException("Interrupted STOR did not start.");
                var output = new DataWriter(dataSocket.OutputStream);
                output.WriteBytes(partialData);
                await output.StoreAsync();
                await dataSocket.CancelIOAsync();
                output.Dispose();
                dataSocket.Dispose();
                await control.CancelIOAsync();
                control.Dispose();
            }

            private async Task<StreamSocket> OpenPassiveAsync()
            {
                await SendAsync("EPSV");
                var reply = await ReadReplyAsync();
                if (reply.Code != 229) throw new InvalidOperationException("EPSV failed.");
                var pieces = reply.Text.Split('|');
                var socket = new StreamSocket();
                await socket.ConnectAsync(new HostName(host), pieces[3]);
                return socket;
            }

            private async Task SendAsync(string command)
            {
                writer.WriteString(command + "\r\n");
                await writer.StoreAsync();
            }

            private async Task<(int Code, string Text)> ReadReplyAsync()
            {
                var first = await ReadLineAsync();
                if (first == null || first.Length < 3) throw new InvalidOperationException("Invalid FTP reply.");
                var code = int.Parse(first.Substring(0, 3));
                if (first.Length > 3 && first[3] == '-')
                {
                    var end = code + " ";
                    string line;
                    do { line = await ReadLineAsync(); } while (!line.StartsWith(end, StringComparison.Ordinal));
                }
                return (code, first);
            }

            private async Task<string> ReadLineAsync()
            {
                var result = new StringBuilder();
                while (true)
                {
                    if (await reader.LoadAsync(1) == 0) return result.Length == 0 ? null : result.ToString();
                    var value = (char)reader.ReadByte();
                    if (value == '\n') return result.ToString().TrimEnd('\r');
                    result.Append(value);
                }
            }

            public void Dispose()
            {
                reader.Dispose(); writer.Dispose(); control.Dispose();
            }
        }
    }
}
