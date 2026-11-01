using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Attax.Core;
using Attax.Core.Utils;
using Attax.Server.Contracts.Common;
using Attax.Server.Contracts.Gameplay;
using Attax.Server.Contracts.Lobby;
using Attax.Server.Contracts.Sync;
using Attax.Server.Contracts.Transport;
using Serilog;

namespace Attax.UserSimulator {
    public sealed class LobbyBot {
        private readonly BotOptions _options;
        private readonly ServerConnection _connection;
        private readonly GamePlayer _player;
        private readonly ILogger _log;
        private readonly Random _rng;

        private TaskCompletionSource<PlayerNotificationDto>? _gameStartedTcs;

        public LobbyBot(BotOptions options, ServerConnection connection, GamePlayer player, ILogger log) {
            _options = options;
            _connection = connection;
            _player = player;
            _log = log;
            _rng = options.Seed != 0 ? new Random(options.Seed) : new Random();
        }

        public async Task RunAsync(CancellationToken ct = default) {
            _connection.OnPlayerNotification += HandleNotification;

            int gamesPlayed = 0;
            try {
                while (!ct.IsCancellationRequested) {
                    if (_options.Games > 0 && gamesPlayed >= _options.Games) {
                        _log.Information("Target game count {Target} reached. Exiting lobby loop.", _options.Games);
                        break;
                    }

                    _gameStartedTcs = new TaskCompletionSource<PlayerNotificationDto>(TaskCreationOptions.RunContinuationsAsynchronously);

                    // Check if ResumeGame returns an active game
                    var resume = await _connection.CallAsync<ResumeGameResponse>("ResumeGame");
                    if (resume.HasGame && resume.IsActive && !string.IsNullOrEmpty(resume.GameId) && resume.Snapshot != null) {
                        _log.Information("Found active game to resume: {GameId}", resume.GameId);
                        var myColor = resume.Snapshot.Seats.RedPlayerId == _connection.PlayerId ? PlayerColor.Red : PlayerColor.Blue;
                        await _player.PlayAsync(resume.GameId, myColor, ct);
                        gamesPlayed++;
                        continue;
                    }

                    if (_options.LobbyBehavior == "wait") {
                        // Wait mode: wait until challenged
                        _log.Debug("Bot is in 'wait' mode. Waiting for incoming challenge...");
                        var started = await _gameStartedTcs.Task.WaitAsync(ct);
                        var myColor = started.AssignedColor == PlayerColorDto.Red ? PlayerColor.Red : PlayerColor.Blue;
                        await _player.PlayAsync(started.GameId!, myColor, ct);
                        gamesPlayed++;
                    } else {
                        // Search mode: poll lobby and challenge
                        int jitter = (int)(_options.LobbyPollMs * 0.3);
                        int delay = _options.LobbyPollMs + _rng.Next(-jitter, jitter + 1);

                        var completedTask = await Task.WhenAny(_gameStartedTcs.Task, Task.Delay(delay, ct));
                        if (completedTask == _gameStartedTcs.Task) {
                            var started = await _gameStartedTcs.Task;
                            var myColor = started.AssignedColor == PlayerColorDto.Red ? PlayerColor.Red : PlayerColor.Blue;
                            await _player.PlayAsync(started.GameId!, myColor, ct);
                            gamesPlayed++;
                            continue;
                        }

                        if (_rng.NextDouble() < _options.ChallengeProbability) {
                            await TryIssueChallengeAsync(ct);
                        }
                    }
                }
            } finally {
                _connection.OnPlayerNotification -= HandleNotification;
            }
        }

        private async Task TryIssueChallengeAsync(CancellationToken ct) {
            try {
                using var msg = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.LobbyPlayersFull);
                _connection.AddHeaders(msg);
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _connection.AccessToken);

                var resp = await _connection.Http.SendAsync(msg, ct);
                if (!resp.IsSuccessStatusCode) return;

                var lobby = await resp.Content.ReadFromJsonAsync<LobbySummaryDto>(cancellationToken: ct);
                if (lobby == null || lobby.Players.Count == 0) return;

                var candidates = lobby.Players.Where(p => p.PlayerId != _connection.PlayerId && p.IsAvailable).ToList();
                if (candidates.Count == 0) return;

                var target = candidates[_rng.Next(candidates.Count)];
                _log.Information("Issuing challenge to {TargetId} ({DisplayName})", target.PlayerId, target.DisplayName);

                var req = new CreateChallengeRequest { TargetPlayerId = target.PlayerId };
                await _connection.CallAsync<ChallengeDto>("CreateChallenge", req);
            } catch (ServerErrorException ex) {
                _log.Debug("Challenge failed: {Code}", ex.Code);
            } catch (Exception ex) {
                _log.Warning(ex, "Failed to issue challenge");
            }
        }

        private void HandleNotification(PlayerNotificationDto notif) {
            if (notif.Type == PlayerNotificationTypes.ChallengeReceived && notif.Challenge != null) {
                var c = notif.Challenge;
                Task.Run(async () => {
                    try {
                        if (_options.LobbyBehavior == "search") {
                            _log.Information("Declining incoming challenge {ChallengeId} (search-only bot)", c.ChallengeId);
                            var resp = new RespondChallengeRequest { Accept = false };
                            await _connection.CallAsync<ChallengeDto>("RespondChallenge", c.ChallengeId, resp);
                        } else {
                            int waitMs = _options.ParsedThinkMs.Max > 0 ? _rng.Next(0, 2000) : 0;
                            if (waitMs > 0) await Task.Delay(waitMs);
                            _log.Information("Auto-accepting challenge {ChallengeId}", c.ChallengeId);
                            var resp = new RespondChallengeRequest { Accept = true };
                            await _connection.CallAsync<ChallengeDto>("RespondChallenge", c.ChallengeId, resp);
                        }
                    } catch (Exception ex) {
                        _log.Warning(ex, "Failed to respond to challenge {ChallengeId}", c.ChallengeId);
                    }
                });
            } else if (notif.Type == PlayerNotificationTypes.GameStarted && !string.IsNullOrEmpty(notif.GameId)) {
                _log.Information("GameStarted received for {GameId}", notif.GameId);
                _gameStartedTcs?.TrySetResult(notif);
            }
        }
    }
}
