using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Attax.Server.Contracts.Sessions;
using Serilog;
using Xunit;

namespace Attax.UserSimulator.Tests {
    public sealed class ServerConnectionUnitTests : IDisposable {
        private readonly string _tempStateDir;

        public ServerConnectionUnitTests() {
            _tempStateDir = Path.Combine(Path.GetTempPath(), "attax_bot_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempStateDir);
        }

        public void Dispose() {
            try {
                if (Directory.Exists(_tempStateDir)) {
                    Directory.Delete(_tempStateDir, true);
                }
            } catch {
                // Ignore cleanup errors
            }
        }

        [Fact]
        public async Task Recovery_RotatedSecret_IsPersistedAndAssigned() {
            var store = new CredentialStore(_tempStateDir);
            const string botName = "test_bot_persist";
            const string originalPlayerId = "p_bot_original";
            const string originalSecret = "sec_old_111111111111111111111111";
            const string rotatedSecret = "sec_new_222222222222222222222222";

            store.Save(botName, originalPlayerId, originalSecret);

            var handler = new MockHttpMessageHandler(async (req, ct) => {
                if (req.RequestUri?.AbsolutePath == "/api/sessions/recover") {
                    var responseBody = new RecoverSessionResponse {
                        PlayerId = originalPlayerId,
                        AccessToken = "tok_recovered_123",
                        RecoverySecret = rotatedSecret,
                        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
                    };
                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(responseBody);
                    return new HttpResponseMessage(HttpStatusCode.OK) {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var options = new BotOptions {
                Name = botName,
                StateDir = _tempStateDir,
                Server = "http://localhost:5000",
                ClientVersion = "1.0.0"
            };

            var connection = new ServerConnection(options, Log.Logger, () => handler, new ImmediateTimeProvider());
            await connection.AuthenticateSessionAsync(CancellationToken.None);

            Assert.Equal(rotatedSecret, connection.RecoverySecret);

            var saved = store.Load(botName);
            Assert.NotNull(saved);
            Assert.Equal(originalPlayerId, saved.PlayerId);
            Assert.Equal(rotatedSecret, saved.RecoverySecret);
        }

        [Fact]
        public async Task Recovery_Unauthorized_ThrowsAndPreservesExistingCredentials() {
            var store = new CredentialStore(_tempStateDir);
            const string botName = "test_bot_unauth";
            const string originalPlayerId = "p_bot_victim";
            const string originalSecret = "sec_retired_old";

            store.Save(botName, originalPlayerId, originalSecret);

            int recoveryAttempts = 0;
            int createAttempts = 0;

            var handler = new MockHttpMessageHandler(async (req, ct) => {
                if (req.RequestUri?.AbsolutePath == "/api/sessions/recover") {
                    recoveryAttempts++;
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized) {
                        Content = new StringContent("{\"error\":\"SECRET_ROTATED\"}", Encoding.UTF8, "application/json")
                    };
                }
                if (req.RequestUri?.AbsolutePath == "/api/sessions") {
                    createAttempts++;
                    return new HttpResponseMessage(HttpStatusCode.Created) {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var options = new BotOptions {
                Name = botName,
                StateDir = _tempStateDir,
                Server = "http://localhost:5000",
                ClientVersion = "1.0.0"
            };

            var connection = new ServerConnection(options, Log.Logger, () => handler, new ImmediateTimeProvider());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                connection.AuthenticateSessionAsync(CancellationToken.None));

            Assert.Contains("--new-identity", ex.Message);
            Assert.Equal(1, recoveryAttempts);
            Assert.Equal(0, createAttempts); // MUST NOT attempt silent creation

            // Credentials must remain untouched on disk
            var saved = store.Load(botName);
            Assert.NotNull(saved);
            Assert.Equal(originalPlayerId, saved.PlayerId);
            Assert.Equal(originalSecret, saved.RecoverySecret);
        }

        [Fact]
        public async Task Recovery_RateLimited_RetriesAndSucceeds() {
            var store = new CredentialStore(_tempStateDir);
            const string botName = "test_bot_retry";
            const string originalPlayerId = "p_bot_retry";
            const string originalSecret = "sec_retry_secret";
            const string rotatedSecret = "sec_rotated_after_retry";

            store.Save(botName, originalPlayerId, originalSecret);

            int recoveryAttempts = 0;

            var handler = new MockHttpMessageHandler(async (req, ct) => {
                if (req.RequestUri?.AbsolutePath == "/api/sessions/recover") {
                    recoveryAttempts++;
                    if (recoveryAttempts == 1) {
                        var resp429 = new HttpResponseMessage((HttpStatusCode)429) {
                            Content = new StringContent("{\"error\":\"RECOVERY_RATE_LIMITED\"}", Encoding.UTF8, "application/json")
                        };
                        resp429.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                        return resp429;
                    }

                    var responseBody = new RecoverSessionResponse {
                        PlayerId = originalPlayerId,
                        AccessToken = "tok_recovered_after_retry",
                        RecoverySecret = rotatedSecret,
                        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
                    };
                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(responseBody);
                    return new HttpResponseMessage(HttpStatusCode.OK) {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var options = new BotOptions {
                Name = botName,
                StateDir = _tempStateDir,
                Server = "http://localhost:5000",
                ClientVersion = "1.0.0"
            };

            var connection = new ServerConnection(options, Log.Logger, () => handler, new ImmediateTimeProvider());
            await connection.AuthenticateSessionAsync(CancellationToken.None);

            Assert.Equal(2, recoveryAttempts);
            Assert.Equal(rotatedSecret, connection.RecoverySecret);
        }

        [Fact]
        public async Task Recovery_RateLimited_Over60Seconds_AbortsCleanly() {
            var store = new CredentialStore(_tempStateDir);
            const string botName = "test_bot_long_wait";
            store.Save(botName, "p_long", "sec_long");

            int recoveryAttempts = 0;

            var handler = new MockHttpMessageHandler(async (req, ct) => {
                if (req.RequestUri?.AbsolutePath == "/api/sessions/recover") {
                    recoveryAttempts++;
                    var resp429 = new HttpResponseMessage((HttpStatusCode)429) {
                        Content = new StringContent("{\"error\":\"RECOVERY_RATE_LIMITED\"}", Encoding.UTF8, "application/json")
                    };
                    resp429.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(900));
                    return resp429;
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var options = new BotOptions {
                Name = botName,
                StateDir = _tempStateDir,
                Server = "http://localhost:5000",
                ClientVersion = "1.0.0"
            };

            var connection = new ServerConnection(options, Log.Logger, () => handler, new ImmediateTimeProvider());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                connection.AuthenticateSessionAsync(CancellationToken.None));

            Assert.Contains("900s", ex.Message);
            Assert.Equal(1, recoveryAttempts);
        }

        [Fact]
        public async Task Creation_WithoutExistingCredentials_SavesNewCredentials() {
            var store = new CredentialStore(_tempStateDir);
            const string botName = "test_bot_fresh";
            const string newPlayerId = "p_bot_fresh_123";
            const string newSecret = "sec_fresh_created";

            var handler = new MockHttpMessageHandler(async (req, ct) => {
                if (req.RequestUri?.AbsolutePath == "/api/sessions") {
                    var responseBody = new CreateSessionResponse {
                        PlayerId = newPlayerId,
                        RecoverySecret = newSecret,
                        AccessToken = "tok_fresh_access",
                        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
                    };
                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(responseBody);
                    return new HttpResponseMessage(HttpStatusCode.Created) {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var options = new BotOptions {
                Name = botName,
                StateDir = _tempStateDir,
                Server = "http://localhost:5000",
                ClientVersion = "1.0.0"
            };

            var connection = new ServerConnection(options, Log.Logger, () => handler, new ImmediateTimeProvider());
            await connection.AuthenticateSessionAsync(CancellationToken.None);

            Assert.Equal(newPlayerId, connection.PlayerId);
            Assert.Equal(newSecret, connection.RecoverySecret);

            var saved = store.Load(botName);
            Assert.NotNull(saved);
            Assert.Equal(newPlayerId, saved.PlayerId);
            Assert.Equal(newSecret, saved.RecoverySecret);
        }

        private ServerConnection CreateRefreshConnection(string botName, DateTimeOffset firstExpiry, Func<HttpRequestMessage, HttpResponseMessage> lobbyHandler, Action onRecover, out Func<int> recoverCount) {
            int recovers = 0;
            recoverCount = () => recovers;

            var handler = new MockHttpMessageHandler((req, ct) => {
                string path = req.RequestUri?.AbsolutePath ?? string.Empty;
                if (path == "/api/sessions") {
                    var body = new CreateSessionResponse {
                        PlayerId = "p_refresh",
                        RecoverySecret = "sec_1",
                        AccessToken = "tok_1",
                        ExpiresAtUtc = firstExpiry
                    };
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) {
                        Content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json")
                    });
                }
                if (path == "/api/sessions/recover") {
                    int n = Interlocked.Increment(ref recovers);
                    onRecover();
                    var body = new RecoverSessionResponse {
                        PlayerId = "p_refresh",
                        RecoverySecret = "sec_" + (n + 1),
                        AccessToken = "tok_" + (n + 1),
                        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
                    };
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                        Content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json")
                    });
                }
                if (path == "/api/lobby/players") {
                    return Task.FromResult(lobbyHandler(req));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

            var options = new BotOptions {
                Name = botName,
                StateDir = _tempStateDir,
                Server = "http://localhost:5000",
                ClientVersion = "1.0.0"
            };
            return new ServerConnection(options, Log.Logger, () => handler, new ImmediateTimeProvider());
        }

        [Fact]
        public async Task SendAuthenticated_TokenNearExpiry_RefreshesProactivelyBeforeRequest() {
            var seenTokens = new System.Collections.Generic.List<string?>();
            var connection = CreateRefreshConnection(
                "test_bot_proactive",
                DateTimeOffset.UtcNow.AddMinutes(2), // within the 5 minute refresh margin
                req => {
                    seenTokens.Add(req.Headers.Authorization?.Parameter);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                },
                () => { },
                out var recoverCount);

            await connection.AuthenticateSessionAsync(CancellationToken.None);
            Assert.Equal("tok_1", connection.AccessToken);

            using var resp = await connection.SendAuthenticatedAsync(() => new HttpRequestMessage(HttpMethod.Get, "/api/lobby/players"), CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, recoverCount());
            Assert.Equal("tok_2", connection.AccessToken);
            Assert.Single(seenTokens);
            Assert.Equal("tok_2", seenTokens[0]);
            Assert.True(connection.AccessTokenExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(30));
        }

        [Fact]
        public async Task SendAuthenticated_ValidToken_DoesNotRefresh() {
            var connection = CreateRefreshConnection(
                "test_bot_norefresh",
                DateTimeOffset.UtcNow.AddHours(1),
                req => new HttpResponseMessage(HttpStatusCode.OK),
                () => { },
                out var recoverCount);

            await connection.AuthenticateSessionAsync(CancellationToken.None);
            using var resp = await connection.SendAuthenticatedAsync(() => new HttpRequestMessage(HttpMethod.Get, "/api/lobby/players"), CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(0, recoverCount());
            Assert.Equal("tok_1", connection.AccessToken);
        }

        [Fact]
        public async Task SendAuthenticated_Unauthorized_RefreshesOnceAndRetries() {
            int lobbyCalls = 0;
            var connection = CreateRefreshConnection(
                "test_bot_401",
                DateTimeOffset.UtcNow.AddHours(1),
                req => {
                    Interlocked.Increment(ref lobbyCalls);
                    return req.Headers.Authorization?.Parameter == "tok_1"
                        ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                        : new HttpResponseMessage(HttpStatusCode.OK);
                },
                () => { },
                out var recoverCount);

            await connection.AuthenticateSessionAsync(CancellationToken.None);
            using var resp = await connection.SendAuthenticatedAsync(() => new HttpRequestMessage(HttpMethod.Get, "/api/lobby/players"), CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(2, lobbyCalls);
            Assert.Equal(1, recoverCount());
            Assert.Equal("tok_2", connection.AccessToken);
        }

        [Fact]
        public async Task SendAuthenticated_PersistentUnauthorized_RetriesOnlyOnce() {
            int lobbyCalls = 0;
            var connection = CreateRefreshConnection(
                "test_bot_401_twice",
                DateTimeOffset.UtcNow.AddHours(1),
                req => {
                    Interlocked.Increment(ref lobbyCalls);
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                },
                () => { },
                out var recoverCount);

            await connection.AuthenticateSessionAsync(CancellationToken.None);
            using var resp = await connection.SendAuthenticatedAsync(() => new HttpRequestMessage(HttpMethod.Get, "/api/lobby/players"), CancellationToken.None);

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Equal(2, lobbyCalls);
            Assert.Equal(1, recoverCount());
        }

        [Fact]
        public async Task Authenticate_TracksAccessTokenExpiry() {
            var expiry = DateTimeOffset.UtcNow.AddHours(3);
            var connection = CreateRefreshConnection("test_bot_expiry", expiry, req => new HttpResponseMessage(HttpStatusCode.OK), () => { }, out _);

            await connection.AuthenticateSessionAsync(CancellationToken.None);

            Assert.Equal(expiry.ToUnixTimeSeconds(), connection.AccessTokenExpiresAtUtc.ToUnixTimeSeconds());
        }

        private sealed class ImmediateTimeProvider : TimeProvider {
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
                callback(state);
                return new DummyTimer();
            }

            private sealed class DummyTimer : ITimer {
                public bool Change(TimeSpan dueTime, TimeSpan period) => true;
                public void Dispose() { }
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
        }

        private sealed class MockHttpMessageHandler : HttpMessageHandler {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

            public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
                return _handler(request, cancellationToken);
            }
        }
    }
}
