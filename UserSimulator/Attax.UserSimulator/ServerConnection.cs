using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Attax.Server.Contracts.Common;
using Attax.Server.Contracts.Serialization;
using Attax.Server.Contracts.Sessions;
using Attax.Server.Contracts.Sync;
using Attax.Server.Contracts.Transport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
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
        private readonly TimeProvider _timeProvider;

        private HttpClient _http = default!;
        private HubConnection _hub = default!;
        private string _playerId = string.Empty;
        private string _recoverySecret = string.Empty;
        private string _accessToken = string.Empty;
        private long _seq = 0;
        private Timer? _heartbeatTimer;

        public string PlayerId => _playerId;
        public string AccessToken => _accessToken;
        public string RecoverySecret => _recoverySecret;
        public HubConnection Hub => _hub;
        public HttpClient Http => _http;

        public event Action<PlayerNotificationDto>? OnPlayerNotification;
        public event Action<string, GameEventEnvelopeDto>? OnGameEvent;
        public event Action<string>? OnSubscriptionLagged;

        public ServerConnection(BotOptions options, ILogger log, Func<HttpMessageHandler>? httpHandlerFactory = null, TimeProvider? timeProvider = null) {
            _options = options;
            _log = log;
            _store = new CredentialStore(options.StateDir);
            _httpHandlerFactory = httpHandlerFactory;
            _timeProvider = timeProvider ?? TimeProvider.System;
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

        private TimeSpan? ParseRetryAfter(HttpResponseMessage resp) {
            if (resp.Headers.RetryAfter == null) return null;
            if (resp.Headers.RetryAfter.Delta.HasValue) {
                var delta = resp.Headers.RetryAfter.Delta.Value;
                return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            }
            if (resp.Headers.RetryAfter.Date.HasValue) {
                var diff = resp.Headers.RetryAfter.Date.Value - _timeProvider.GetUtcNow();
                return diff < TimeSpan.Zero ? TimeSpan.Zero : diff;
            }
            return null;
        }

        internal async Task AuthenticateSessionAsync(CancellationToken ct) {
            if (_http == null) {
                var handler = _httpHandlerFactory != null ? _httpHandlerFactory() : new HttpClientHandler();
                _http = new HttpClient(handler) {
                    BaseAddress = new Uri(_options.Server)
                };
                _http.DefaultRequestHeaders.Add(TransportHeaders.ClientVersion, _options.ClientVersion);
            }

            var existing = _options.NewIdentity ? null : _store.Load(_options.Name);
            if (existing != null) {
                _log.Information("Attempting to recover existing session for {PlayerId}", existing.PlayerId);
                var recoverReq = new RecoverSessionRequest {
                    PlayerId = existing.PlayerId,
                    RecoverySecret = existing.RecoverySecret,
                    ClientVersion = _options.ClientVersion
                };

                int attempt = 0;
                while (true) {
                    using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/recover") {
                        Content = ServerJson.ToJsonContent(recoverReq)
                    };
                    AddHeaders(msg);

                    HttpResponseMessage resp;
                    try {
                        resp = await _http.SendAsync(msg, ct);
                    } catch (Exception ex) when (!ct.IsCancellationRequested) {
                        if (attempt < 3) {
                            var fallbackDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                            _log.Warning("Recovery network failure ({ErrorType}). Retrying in {DelayMs}ms (attempt {Attempt}/3)", ex.GetType().Name, fallbackDelay.TotalMilliseconds, attempt + 1);
                            attempt++;
                            await Task.Delay(fallbackDelay, _timeProvider, ct);
                            continue;
                        }
                        throw new InvalidOperationException($"Recovery failed due to network error after retries: {ex.Message}", ex);
                    }

                    using (resp) {
                        if (resp.IsSuccessStatusCode) {
                            var recovered = await ServerJson.ReadJsonAsync<RecoverSessionResponse>(resp.Content, ct);
                            if (recovered == null || string.IsNullOrWhiteSpace(recovered.RecoverySecret)) {
                                throw new InvalidOperationException("Recovery response did not include a valid rotated recovery secret.");
                            }
                            _playerId = recovered.PlayerId;
                            _recoverySecret = recovered.RecoverySecret;
                            _accessToken = recovered.AccessToken;
                            try {
                                _store.Save(_options.Name, _playerId, _recoverySecret);
                            } catch (Exception ex) {
                                throw new InvalidOperationException($"Failed to persist rotated recovery secret for '{_options.Name}': {ex.Message}", ex);
                            }
                            _log.Information("Successfully recovered session for {PlayerId}", _playerId);
                            return;
                        }

                        bool isRetryable = resp.StatusCode == (HttpStatusCode)429 || ((int)resp.StatusCode >= 500 && (int)resp.StatusCode <= 599);
                        if (isRetryable && attempt < 3) {
                            var retryAfter = ParseRetryAfter(resp);
                            if (retryAfter.HasValue && retryAfter.Value.TotalSeconds > 60) {
                                throw new InvalidOperationException($"Recovery rate limited with Retry-After {retryAfter.Value.TotalSeconds:F0}s exceeding 60s limit. Aborting.");
                            }
                            var fallbackDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                            var delay = retryAfter.HasValue && retryAfter.Value > fallbackDelay ? retryAfter.Value : fallbackDelay;
                            _log.Warning("Recovery returned {StatusCode}. Retrying in {DelayMs}ms (attempt {Attempt}/3)", (int)resp.StatusCode, delay.TotalMilliseconds, attempt + 1);
                            attempt++;
                            await Task.Delay(delay, _timeProvider, ct);
                            continue;
                        }

                        // Preserves existing credential file; gives actionable error directing replacement
                        string actionGuidance = resp.StatusCode == HttpStatusCode.Unauthorized
                            ? " Use --new-identity to register a replacement identity."
                            : "";
                        throw new InvalidOperationException(
                            $"Recovery failed with HTTP {(int)resp.StatusCode} ({resp.StatusCode}).{actionGuidance}");
                    }
                }
            }

            var createReq = new CreateSessionRequest {
                DisplayName = _options.Name,
                ClientVersion = _options.ClientVersion
            };

            int createAttempt = 0;
            while (true) {
                using var createMsg = new HttpRequestMessage(HttpMethod.Post, "/api/sessions") {
                    Content = ServerJson.ToJsonContent(createReq)
                };
                AddHeaders(createMsg);

                HttpResponseMessage createResp;
                try {
                    createResp = await _http.SendAsync(createMsg, ct);
                } catch (Exception ex) when (!ct.IsCancellationRequested) {
                    throw new InvalidOperationException($"Failed to create session due to network error: {ex.Message}", ex);
                }

                using (createResp) {
                    if (createResp.IsSuccessStatusCode) {
                        var created = (await ServerJson.ReadJsonAsync<CreateSessionResponse>(createResp.Content, ct))!;
                        if (string.IsNullOrWhiteSpace(created.RecoverySecret)) {
                            throw new InvalidOperationException("Create session response did not include a recovery secret.");
                        }
                        _playerId = created.PlayerId;
                        _recoverySecret = created.RecoverySecret;
                        _accessToken = created.AccessToken;
                        _store.Save(_options.Name, _playerId, _recoverySecret);
                        _log.Information("Created new session for {PlayerId}", _playerId);
                        return;
                    }

                    if (createResp.StatusCode == (HttpStatusCode)429 && createAttempt < 3) {
                        var retryAfter = ParseRetryAfter(createResp);
                        if (retryAfter.HasValue && retryAfter.Value.TotalSeconds > 60) {
                            throw new InvalidOperationException($"Creation rate limited with Retry-After {retryAfter.Value.TotalSeconds:F0}s exceeding 60s limit.");
                        }
                        var fallbackDelay = TimeSpan.FromSeconds(Math.Pow(2, createAttempt));
                        var delay = retryAfter.HasValue && retryAfter.Value > fallbackDelay ? retryAfter.Value : fallbackDelay;
                        _log.Warning("Creation rate limited (429). Retrying in {DelayMs}ms (attempt {Attempt}/3)", delay.TotalMilliseconds, createAttempt + 1);
                        createAttempt++;
                        await Task.Delay(delay, _timeProvider, ct);
                        continue;
                    }

                    throw new ServerErrorException(
                        createResp.StatusCode.ToString(),
                        $"Create session failed with HTTP {(int)createResp.StatusCode}");
                }
            }
        }

        private async Task ConnectHubAsync(CancellationToken ct) {
            string ticket = await IssueHubTicketAsync(ct);

            var hubUri = new Uri(new Uri(_options.Server), $"/hubs/game?{TransportQuery.ClientVersion}={_options.ClientVersion}&{TransportQuery.CorrelationId}={_options.Name}&{TransportQuery.Ticket}={Uri.EscapeDataString(ticket)}");

            var builder = new HubConnectionBuilder()
                .WithUrl(hubUri, o => {
                    if (_httpHandlerFactory != null) {
                        o.HttpMessageHandlerFactory = _ => _httpHandlerFactory();
                    }
                    // The hub ticket is single use and only re-accepted within 10 s of the first request (server InMemoryHubTicketService),
                    // so LongPolling (a new request with the same ticket every poll) breaks after ~15 s against a real server.
                    // LongPolling is kept only for the in-process test server, which has no WebSocket support.
                    o.Transports = _httpHandlerFactory != null ? HttpTransportType.LongPolling : HttpTransportType.WebSockets;
                })
                .AddNewtonsoftJsonProtocol(o => AttaxJsonSettings.Apply(o.PayloadSerializerSettings))
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
            var body = await ServerJson.ReadJsonAsync<HubTicketResponse>(resp.Content, ct);
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
