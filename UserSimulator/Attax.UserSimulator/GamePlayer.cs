using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Attax.Agents;
using Attax.Client.Protocol;
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
            _rng = options.Seed != 0 ? new Random(options.Seed) : new Random(); // NOSONAR
        }

        public async Task<GameOutcomeDto> PlayAsync(string gameId, PlayerColor myColor, CancellationToken ct = default) {
            using (LogContext.PushProperty("GameId", gameId))
            using (LogContext.PushProperty("Color", myColor.ToString())) {
                _log.Information("Starting gameplay loop as {Color} in game {GameId}", myColor, gameId);

                var reconciler = new GameSyncReconciler<Newtonsoft.Json.Linq.JToken>();
                bool isSubmitting = false;
                bool needsResync = false;
                bool needsResubscribe = false;

                Action onReconnected = () => {
                    needsResync = true;
                    needsResubscribe = true;
                };

                Action<string, GameEventEnvelopeDto> onEvent = (gId, env) => {
                    if (gId != gameId) return;
                    _log.Information("GameEvent received: v{Version} {Type}", env.ServerVersion, env.Type);
                    var decision = reconciler.OnEvent(new SyncEvent<Newtonsoft.Json.Linq.JToken>(env.ServerVersion, env.Payload, env.IsTerminal));
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
                _connection.OnReconnected += onReconnected;

                try {
                    // Subscribe-first
                    var subReply = await _connection.CallAsync<SubscribeGameResponseDto>("SubscribeGame", gameId);
                    bool initialTerminal = subReply.Snapshot.Status != GameLifecycleStatusDto.Active && subReply.Snapshot.Status != GameLifecycleStatusDto.WaitingForPlayers;
                    reconciler.OnSnapshot(subReply.Snapshot.ServerVersion, initialTerminal);

                    var state = SnapshotMapper.ToState(subReply.Snapshot);
                    long currentServerVersion = subReply.Snapshot.ServerVersion;

                    if (initialTerminal) {
                        _log.Information("Game {GameId} finished with status {Status} (outcome: {Outcome})", gameId, subReply.Snapshot.Status, subReply.Snapshot.Outcome);
                        return new GameOutcomeDto { GameId = gameId, Result = subReply.Snapshot.Outcome.ToString() };
                    }

                    while (!ct.IsCancellationRequested) {
                        if (needsResync) {
                            needsResync = false;
                            if (needsResubscribe) {
                                needsResubscribe = false;
                                try {
                                    await _connection.CallAsync<SubscribeGameResponseDto>("SubscribeGame", gameId);
                                } catch (ServerErrorException ex) {
                                    _log.Warning(ex, "Failed to resubscribe to game {GameId}", gameId);
                                }
                            }
                            var freshSnapshot = await FetchSnapshotAsync(gameId, ct);
                            bool isTerminalStatus = freshSnapshot.Status != GameLifecycleStatusDto.Active && freshSnapshot.Status != GameLifecycleStatusDto.WaitingForPlayers;
                            reconciler.OnSnapshot(freshSnapshot.ServerVersion, isTerminalStatus);
                            state = SnapshotMapper.ToState(freshSnapshot);
                            currentServerVersion = freshSnapshot.ServerVersion;

                            if (isTerminalStatus) {
                                var outcome = new GameOutcomeDto {
                                    GameId = gameId,
                                    Result = freshSnapshot.Outcome.ToString()
                                };
                                _log.Information("Game {GameId} finished with status {Status} (outcome: {Outcome})", gameId, freshSnapshot.Status, freshSnapshot.Outcome);
                                return outcome;
                            }
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
                                var intent = ActionIntentMapper.ToIntent(action);
                                var submitReq = new SubmitActionRequest {
                                    RequestId = reqId,
                                    ExpectedVersion = currentServerVersion,
                                    Action = intent
                                };

                                var res = await _connection.CallAsync<SubmitActionResponse>("SubmitAction", gameId, submitReq);
                                _log.Debug("Action {ReqId} executed cleanly. Success: {Success}", reqId, res.Success);
                                state = SnapshotMapper.ToState(res.Snapshot);
                                currentServerVersion = res.ServerVersion;
                            } catch (ServerErrorException ex) when (ErrorCodeClassifier.IsStaleState(ex.Code)) {
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
                    _connection.OnReconnected -= onReconnected;
                }
            }
        }

        private async Task<GameSnapshotDto> FetchSnapshotAsync(string gameId, CancellationToken ct) {
            using var resp = await _connection.SendAuthenticatedAsync(() => {
                var msg = new HttpRequestMessage(HttpMethod.Get, $"/api/games/{gameId}");
                _connection.AddHeaders(msg);
                return msg;
            }, ct);
            resp.EnsureSuccessStatusCode();
            return (await ServerJson.ReadJsonAsync<GameSnapshotDto>(resp.Content, ct))!;
        }
    }

    public sealed class GameOutcomeDto {
        public string GameId { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
    }
}
