using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Attax.Server.Contracts.Common;
using Attax.Server.Contracts.Sessions;
using Attax.Server.Contracts.Sync;
using Attax.Server.Contracts.Transport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Serilog;

namespace Attax.UserSimulator {
    public sealed class ServerErrorException : Exception {
        public string Code { get; }
        public ServerErrorException(string code, string message) : base($"Server error [{code}]: {message}") {
            Code = code;
        }
    }

    public sealed class ServerConnection : IAsyncDisposable {
        private readonly BotOptions _options;
        private readonly ILogger _log;
        private readonly CredentialStore _store;
        private readonly Func<HttpMessageHandler>? _httpHandlerFactory;

        private HttpClient _http = default!;
        private HubConnection _hub = default!;
        private string _playerId = string.Empty;
        private string _recoverySecret = string.Empty;
        private string _accessToken = string.Empty;
        private long _seq = 0;
        private Timer? _heartbeatTimer;

        public string PlayerId => _playerId;
        public string AccessToken => _accessToken;
        public HubConnection Hub => _hub;
        public HttpClient Http => _http;

        public event Action<PlayerNotificationDto>? OnPlayerNotification;
        public event Action<string, GameEventEnvelopeDto>? OnGameEvent;
        public event Action<string>? OnSubscriptionLagged;

        public ServerConnection(BotOptions options, ILogger log, Func<HttpMessageHandler>? httpHandlerFactory = null) {
            _options = options;
            _log = log;
            _store = new CredentialStore(options.StateDir);
            _httpHandlerFactory = httpHandlerFactory;
        }

        public async Task StartAsync(CancellationToken ct = default) {
            var handler = _httpHandlerFactory != null ? _httpHandlerFactory() : new HttpClientHandler();
            _http = new HttpClient(handler) {
                BaseAddress = new Uri(_options.Server)
            };
            _http.DefaultRequestHeaders.Add(TransportHeaders.ClientVersion, _options.ClientVersion);

            await AuthenticateSessionAsync(ct);
            await ConnectHubAsync(ct);

            try {
                await CallAsync<HeartbeatResponseDto>("Heartbeat");
            } catch (Exception ex) {
                _log.Warning(ex, "Initial heartbeat failed");
            }

            _heartbeatTimer = new Timer(async _ => {
                try {
                    await CallAsync<HeartbeatResponseDto>("Heartbeat");
                } catch (Exception ex) {
                    _log.Warning(ex, "Heartbeat failed");
                }
            }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        private async Task AuthenticateSessionAsync(CancellationToken ct) {
            var existing = _options.NewIdentity ? null : _store.Load(_options.Name);
            if (existing != null) {
                _log.Information("Attempting to recover existing session for {PlayerId}", existing.PlayerId);
                var recoverReq = new RecoverSessionRequest {
                    PlayerId = existing.PlayerId,
                    RecoverySecret = existing.RecoverySecret,
                    ClientVersion = _options.ClientVersion
                };

                using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/recover") {
                    Content = JsonContent.Create(recoverReq)
                };
                AddHeaders(msg);

                var resp = await _http.SendAsync(msg, ct);
                if (resp.IsSuccessStatusCode) {
                    var recovered = (await resp.Content.ReadFromJsonAsync<RecoverSessionResponse>(cancellationToken: ct))!;
                    _playerId = recovered.PlayerId;
                    _recoverySecret = existing.RecoverySecret;
                    _accessToken = recovered.AccessToken;
                    _log.Information("Successfully recovered session for {PlayerId}", _playerId);
                    return;
                }

                _log.Warning("Recovery returned {Status}. Creating fresh session.", resp.StatusCode);
            }

            var createReq = new CreateSessionRequest {
                DisplayName = _options.Name,
                ClientVersion = _options.ClientVersion
            };
            using var createMsg = new HttpRequestMessage(HttpMethod.Post, "/api/sessions") {
                Content = JsonContent.Create(createReq)
            };
            AddHeaders(createMsg);

            var createResp = await _http.SendAsync(createMsg, ct);
            createResp.EnsureSuccessStatusCode();

            var created = (await createResp.Content.ReadFromJsonAsync<CreateSessionResponse>(cancellationToken: ct))!;
            _playerId = created.PlayerId;
            _recoverySecret = created.RecoverySecret;
            _accessToken = created.AccessToken;
            _store.Save(_options.Name, _playerId, _recoverySecret);
            _log.Information("Created new session for {PlayerId}", _playerId);
        }

        private async Task ConnectHubAsync(CancellationToken ct) {
            string ticket = await IssueHubTicketAsync(ct);

            var hubUri = new Uri(new Uri(_options.Server), $"/hubs/game?{TransportQuery.ClientVersion}={_options.ClientVersion}&{TransportQuery.CorrelationId}={_options.Name}&{TransportQuery.Ticket}={Uri.EscapeDataString(ticket)}");

            var builder = new HubConnectionBuilder()
                .WithUrl(hubUri, o => {
                    if (_httpHandlerFactory != null) {
                        o.HttpMessageHandlerFactory = _ => _httpHandlerFactory();
                    }
                    o.Transports = HttpTransportType.LongPolling;
                })
                .WithAutomaticReconnect();

            _hub = builder.Build();

            _hub.On<PlayerNotificationDto>(HubEventNames.PlayerNotification, notif => {
                OnPlayerNotification?.Invoke(notif);
            });
            _hub.On<GameEventEnvelopeDto>(HubEventNames.GameEvent, env => {
                OnGameEvent?.Invoke(env.GameId, env);
            });
            _hub.On<string>(HubEventNames.SubscriptionLagged, gameId => {
                OnSubscriptionLagged?.Invoke(gameId);
            });
            _hub.On(HubEventNames.ConnectionReplaced, () => {
                _log.Error("Connection replaced by another session");
                Environment.Exit(4);
            });

            await _hub.StartAsync(ct);
            _log.Information("Connected to SignalR hub");
        }

        private async Task<string> IssueHubTicketAsync(CancellationToken ct) {
            using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/hub-ticket");
            AddHeaders(msg);
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            var resp = await _http.SendAsync(msg, ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<HubTicketResponse>(cancellationToken: ct);
            return body!.Ticket;
        }

        public void AddHeaders(HttpRequestMessage msg) {
            long seq = Interlocked.Increment(ref _seq);
            msg.Headers.TryAddWithoutValidation(TransportHeaders.CorrelationId, $"{_options.Name}:{seq}");
            msg.Headers.TryAddWithoutValidation(TransportHeaders.ClientVersion, _options.ClientVersion);
        }

        public async Task<T> CallAsync<T>(string method, params object?[] args) {
            var response = await _hub.InvokeCoreAsync<HubResponse<T>>(method, args);
            if (!response.Ok) {
                string code = response.Error?.ErrorCode ?? "UNKNOWN_ERROR";
                string message = response.Error?.Message ?? "Server operation failed";
                throw new ServerErrorException(code, message);
            }
            return response.Data!;
        }

        public async ValueTask DisposeAsync() {
            if (_heartbeatTimer != null) await _heartbeatTimer.DisposeAsync();
            if (_hub != null) await _hub.DisposeAsync();
            _http?.Dispose();
        }
    }
}
