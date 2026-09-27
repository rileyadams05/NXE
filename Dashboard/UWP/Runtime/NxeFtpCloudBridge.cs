using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client;
using Windows.Data.Json;

namespace NxeDashboard.Runtime
{
    /// <summary>
    /// Bridges NXE with the Vortex Prime cloud relay using the proven Websocket.Client library.
    /// Manages registration, periodic status polling, and real-time command execution via WebSocket.
    /// </summary>
    public sealed class NxeFtpCloudBridge : IDisposable
    {
        private const string ApiBase = "https://vortex-prime-emu.com/api/nxe/pair/";
        private readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private readonly string pairId;
        private readonly string deviceToken;
        private readonly string controlToken;
        private readonly Func<bool> isFtpRunning;
        private readonly Func<string> consoleAddress;
        private readonly Func<string> failure;
        private readonly Func<string, string, Task> configure;
        private readonly Func<Task> start;
        private readonly Func<Task> stop;
        private readonly Func<Task> restart;
        private readonly NxeWebManagementServer managementServer;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Task pollingTask;
        private WebsocketClient websocketClient;
        private string acknowledgedCommandId = string.Empty;
        private bool registered;

        public event EventHandler StateChanged;
        public bool IsConnected { get; private set; }
        public string LastError { get; private set; } = string.Empty;

        public NxeFtpCloudBridge(string pairId, string deviceToken, string controlToken,
            Func<bool> isFtpRunning, Func<string> consoleAddress, Func<string> failure,
            Func<string, string, Task> configure, Func<Task> start, Func<Task> stop, Func<Task> restart,
            NxeWebManagementServer managementServer)
        {
            this.pairId = pairId ?? throw new ArgumentNullException(nameof(pairId));
            this.deviceToken = deviceToken ?? throw new ArgumentNullException(nameof(deviceToken));
            this.controlToken = controlToken ?? throw new ArgumentNullException(nameof(controlToken));
            this.isFtpRunning = isFtpRunning ?? throw new ArgumentNullException(nameof(isFtpRunning));
            this.consoleAddress = consoleAddress ?? throw new ArgumentNullException(nameof(consoleAddress));
            this.failure = failure ?? (() => string.Empty);
            this.configure = configure ?? throw new ArgumentNullException(nameof(configure));
            this.start = start ?? throw new ArgumentNullException(nameof(start));
            this.stop = stop ?? throw new ArgumentNullException(nameof(stop));
            this.restart = restart ?? throw new ArgumentNullException(nameof(restart));
            this.managementServer = managementServer ?? throw new ArgumentNullException(nameof(managementServer));
        }

        public async Task StartAsync()
        {
            if (pollingTask != null) return;
            try { await RegisterAsync(cancellation.Token); }
            catch (Exception exception)
            {
                IsConnected = false;
                LastError = exception.Message;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            try { StartRelay(); }
            catch (Exception exception) { LastError = exception.Message; }
            pollingTask = PollLoopAsync(cancellation.Token);
            await Task.CompletedTask;
        }

        private void StartRelay()
        {
            if (websocketClient != null) return;
            var uri = new Uri("wss://vortex-prime-emu.com/api/nxe/relay/console?pairId=" +
                Uri.EscapeDataString(pairId) + "&deviceToken=" + Uri.EscapeDataString(deviceToken));

            var socketFactory = new Func<ClientWebSocket>(() =>
            {
                var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("X-NXE-Device", deviceToken);
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                return socket;
            });

            var client = new WebsocketClient(uri, socketFactory)
            {
                ReconnectTimeout = TimeSpan.FromSeconds(30),
                ErrorReconnectTimeout = TimeSpan.FromSeconds(3)
            };

            client.MessageReceived.Subscribe(async msg =>
            {
                if (msg.MessageType == WebSocketMessageType.Text && !string.IsNullOrEmpty(msg.Text))
                {
                    await OnRelayMessageReceivedAsync(msg.Text);
                }
            });

            client.DisconnectionHappened.Subscribe(info =>
            {
                IsConnected = false;
                if (info.Exception != null) LastError = info.Exception.Message;
                StateChanged?.Invoke(this, EventArgs.Empty);
            });

            client.ReconnectionHappened.Subscribe(info =>
            {
                IsConnected = true;
                LastError = string.Empty;
                StateChanged?.Invoke(this, EventArgs.Empty);
            });

            websocketClient = client;
            _ = client.Start();
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!registered) await RegisterAsync(token);
                    var response = await SendAsync("poll", BuildStatusPayload(), token);
                    IsConnected = true;
                    LastError = string.Empty;
                    await ApplyCommandAsync(response);
                    if (websocketClient == null || !websocketClient.IsRunning) StartRelay();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    registered = false;
                    IsConnected = false;
                    LastError = exception.Message;
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }

                try { await Task.Delay(TimeSpan.FromSeconds(4), token); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task RegisterAsync(CancellationToken token)
        {
            var payload = BuildStatusPayload();
            await SendAsync("register", payload, token);
            registered = true;
            IsConnected = true;
            LastError = string.Empty;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private JsonObject BuildStatusPayload()
        {
            var payload = new JsonObject
            {
                ["pairId"] = JsonValue.CreateStringValue(pairId),
                ["controlToken"] = JsonValue.CreateStringValue(controlToken),
                ["consoleIp"] = JsonValue.CreateStringValue(consoleAddress() ?? string.Empty),
                ["ftpPort"] = JsonValue.CreateStringValue("2121"),
                ["running"] = JsonValue.CreateBooleanValue(isFtpRunning()),
                ["failure"] = JsonValue.CreateStringValue(failure() ?? string.Empty)
            };
            if (!string.IsNullOrEmpty(acknowledgedCommandId))
                payload["ackCommandId"] = JsonValue.CreateStringValue(acknowledgedCommandId);
            return payload;
        }

        private async Task<JsonObject> SendAsync(string operation, JsonObject payload, CancellationToken token)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + operation))
            {
                request.Headers.TryAddWithoutValidation("X-NXE-Device", deviceToken);
                request.Content = new StringContent(payload.Stringify(), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request, token))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("Vortex Prime control service returned " +
                            ((int)response.StatusCode) + ".");
                    JsonObject parsed;
                    return JsonObject.TryParse(body, out parsed) ? parsed : new JsonObject();
                }
            }
        }

        private async Task ApplyCommandAsync(JsonObject response)
        {
            JsonObject command;
            if (!response.TryGetValue("command", out var commandValue) ||
                commandValue.ValueType != JsonValueType.Object) return;
            command = commandValue.GetObject();
            var id = command.GetNamedString("id", string.Empty);
            if (string.IsNullOrEmpty(id) || string.Equals(id, acknowledgedCommandId, StringComparison.Ordinal)) return;

            var type = command.GetNamedString("type", string.Empty);
            if (type == "configure")
                await configure(command.GetNamedString("username", string.Empty),
                    command.GetNamedString("password", string.Empty));
            else if (type == "start")
                await start();
            else if (type == "stop")
                await stop();
            else if (type == "restart")
                await restart();
            else
                return;

            acknowledgedCommandId = id;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private async Task OnRelayMessageReceivedAsync(string text)
        {
            try
            {
                JsonObject request;
                if (!JsonObject.TryParse(text, out request) || request.GetNamedString("type", "") != "request") return;
                var id = request.GetNamedString("id", "");
                if (string.IsNullOrEmpty(id)) return;
                var response = new JsonObject
                {
                    ["type"] = JsonValue.CreateStringValue("response"),
                    ["id"] = JsonValue.CreateStringValue(id),
                    ["ok"] = JsonValue.CreateBooleanValue(true),
                };
                try
                {
                    var operation = request.GetNamedString("operation", "api");
                    if (operation == "upload")
                    {
                        await managementServer.UploadRelayBase64Async(
                            request.GetNamedString("path", "/"),
                            request.GetNamedString("name", ""),
                            request.GetNamedString("data", ""));
                        response["body"] = new JsonObject { ["ok"] = JsonValue.CreateBooleanValue(true) };
                    }
                    else if (operation == "download")
                    {
                        response["data"] = JsonValue.CreateStringValue(await managementServer.DownloadRelayBase64Async(
                            request.GetNamedString("path", "")));
                    }
                    else
                    {
                        response["body"] = await managementServer.ExecuteRelayRequestAsync(
                            request.GetNamedString("method", "GET"),
                            request.GetNamedString("path", "/"),
                            request.GetNamedString("body", ""));
                    }
                }
                catch (Exception exception)
                {
                    response["ok"] = JsonValue.CreateBooleanValue(false);
                    response["message"] = JsonValue.CreateStringValue(exception.Message ?? "NXE relay request failed.");
                }
                SendRelay(response.Stringify());
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void SendRelay(string text)
        {
            var client = websocketClient;
            if (client == null || !client.IsRunning) throw new InvalidOperationException("Vortex Prime relay is disconnected.");
            client.Send(text ?? string.Empty);
        }

        public void Dispose()
        {
            cancellation.Cancel();
            var client = websocketClient;
            websocketClient = null;
            if (client != null)
            {
                try { client.Dispose(); } catch { }
            }
            http.Dispose();
            cancellation.Dispose();
        }
    }
}
