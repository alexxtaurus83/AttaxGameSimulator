using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Attax.Agents;
using Attax.Client.Sync;
using Attax.Core;
using Attax.Core.Utils;
using Attax.Play;
using Attax.Server.Contracts.Actions;
using Attax.Server.Contracts.Common;
using Attax.Server.Contracts.Gameplay;
using Attax.Server.Contracts.Sync;
using Attax.Server.Contracts.Transport;
using Serilog;
using Serilog.Context;

namespace Attax.UserSimulator {
    public sealed class GamePlayer {
        private readonly BotOptions _options;
        private readonly ServerConnection _connection;
        private readonly AgentTurnPlanner _planner;
        private readonly ILogger _log;
        private readonly Random _rng;

        public GamePlayer(BotOptions options, ServerConnection connection, AgentTurnPlanner planner, ILogger log) {
            _options = options;
            _connection = connection;
            _planner = planner;
            _log = log;
            _rng = options.Seed != 0 ? new Random(options.Seed) : new Random();
        }

        public async Task<GameOutcomeDto> PlayAsync(string gameId, PlayerColor myColor, CancellationToken ct = default) {
            using (LogContext.PushProperty("GameId", gameId))
            using (LogContext.PushProperty("Color", myColor.ToString())) {
                _log.Information("Starting gameplay loop as {Color} in game {GameId}", myColor, gameId);

                var reconciler = new GameSyncReconciler<JsonElement>();
                bool isSubmitting = false;
                bool needsResync = false;

                Action<string, GameEventEnvelopeDto> onEvent = (gId, env) => {
                    if (gId != gameId) return;
                    _log.Information("GameEvent received: v{Version} {Type}", env.ServerVersion, env.Type);
                    var decision = reconciler.OnEvent(new SyncEvent<JsonElement>(env.ServerVersion, env.Payload, env.IsTerminal));
                    if (decision.Action == SyncAction.ResyncRequired) {
                        _log.Warning("Reconciler resync required: {Reason}", decision.ResyncReason);
                        needsResync = true;
                    } else if (decision.Action == SyncAction.Apply) {
                        needsResync = true;
                    }
                };

                Action<string> onLag = gId => {
                    if (gId != gameId) return;
                    _log.Warning("Subscription lagged for game {GameId}. Requesting resync.", gId);
                    needsResync = true;
                };

                _connection.OnGameEvent += onEvent;
                _connection.OnSubscriptionLagged += onLag;

                try {
                    // Subscribe-first
                    var subReply = await _connection.CallAsync<SubscribeGameResponseDto>("SubscribeGame", gameId);
                    reconciler.OnSnapshot(subReply.Snapshot.ServerVersion, subReply.Snapshot.Status == GameLifecycleStatusDto.Completed);

                    var state = SnapshotMapper.ToState(subReply.Snapshot);
                    long currentServerVersion = subReply.Snapshot.ServerVersion;

                    while (!ct.IsCancellationRequested) {
                        if (needsResync) {
                            needsResync = false;
                            var freshSnapshot = await FetchSnapshotAsync(gameId, ct);
                            reconciler.OnSnapshot(freshSnapshot.ServerVersion, freshSnapshot.Status == GameLifecycleStatusDto.Completed);
                            state = SnapshotMapper.ToState(freshSnapshot);
                            currentServerVersion = freshSnapshot.ServerVersion;
                        }

                        if (state.Result != TerminalResult.NotOver) {
                            var outcome = new GameOutcomeDto {
                                GameId = gameId,
                                Result = state.Result.ToString()
                            };
                            _log.Information("Game {GameId} finished with result: {Result}", gameId, state.Result);
                            return outcome;
                        }

                        if (state.SideToMove == myColor && !isSubmitting) {
                            isSubmitting = true;
                            try {
                                int thinkDelay = _rng.Next(_options.ParsedThinkMs.Min, _options.ParsedThinkMs.Max + 1);
                                if (thinkDelay > 0) {
                                    await Task.Delay(thinkDelay, ct);
                                }

                                var action = _planner.Decide(state, myColor, out bool isAbility, out var decision);
                                string rendered = BoardRenderer.Render(state.Board, state.SideToMove, state.RedCredits, state.BlueCredits);
                                _log.Debug("Chosen {ActionKind} at turn {Turn}:\n{Board}", action.Kind, state.TurnNumber, rendered);

                                string reqId = Guid.NewGuid().ToString("N");
                                var intent = SnapshotMapper.ToIntent(action);
                                var submitReq = new SubmitActionRequest {
                                    RequestId = reqId,
                                    ExpectedVersion = currentServerVersion,
                                    Action = intent
                                };

                                var res = await _connection.CallAsync<SubmitActionResponse>("SubmitAction", gameId, submitReq);
                                _log.Debug("Action {ReqId} executed cleanly. Success: {Success}", reqId, res.Success);
                                state = SnapshotMapper.ToState(res.Snapshot);
                                currentServerVersion = res.ServerVersion;
                            } catch (ServerErrorException ex) when (ex.Code == "VERSION_CONFLICT" || ex.Code == "NOT_YOUR_TURN") {
                                _log.Warning("Transient action conflict {Code}. Resyncing snapshot.", ex.Code);
                                needsResync = true;
                            } catch (Exception ex) {
                                _log.Error(ex, "Error submitting action");
                                throw;
                            } finally {
                                isSubmitting = false;
                            }
                        }

                        await Task.Delay(50, ct);
                    }

                    return new GameOutcomeDto { GameId = gameId, Result = "Aborted" };
                } finally {
                    _connection.OnGameEvent -= onEvent;
                    _connection.OnSubscriptionLagged -= onLag;
                }
            }
        }

        private async Task<GameSnapshotDto> FetchSnapshotAsync(string gameId, CancellationToken ct) {
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"/api/games/{gameId}");
            _connection.AddHeaders(msg);
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _connection.AccessToken);

            var resp = await _connection.Http.SendAsync(msg, ct);
            resp.EnsureSuccessStatusCode();
            return (await resp.Content.ReadFromJsonAsync<GameSnapshotDto>(cancellationToken: ct))!;
        }
    }

    public sealed class GameOutcomeDto {
        public string GameId { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
    }
}
