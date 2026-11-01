using System;
using Attax.Core;
using Attax.Core.Utils;
using Attax.Play;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Agents {
    public struct GameEnd {
        public bool Terminal;
        public int Plies;
        public int ResultRedPov;   // +1 red, -1 blue, 0 draw
        public BitboardState Board;
    }

    public static class LocalGameRunner {
        /// <summary>
        /// The single game loop used by self-play, arena, and local simulations. Sides strictly alternate (there are no passes): a side that
        /// cannot move makes the position terminal and is decided by AtaxxAIEngine.GetTerminalResult. Reaching maxPlies
        /// without a terminal position is a ply-cap end whose result falls back to piece counts.
        /// </summary>
        public static GameEnd RunGame(AtaxxAIEngine referee, BitboardState board, int startPly, int maxPlies,
                                      Func<int, PlayerColor, BitboardState, MoveDecision> chooser,
                                      Action<int, PlayerColor, BitboardState, MoveDecision>? onMove = null) {
            int ply = startPly;
            while (true) {
                if (referee.IsGameOver(board)) break;
                if (ply >= maxPlies) break;
                var side = ply % 2 == 0 ? PlayerColor.Red : PlayerColor.Blue;
                var d = chooser(ply, side, board);
                if (!d.HasMove) throw new InvalidOperationException($"No move returned at ply {ply} although the game is not over.");
                if (!ActionCodec.IsLegal(d.Action, board, side))
                    throw new InvalidOperationException($"Illegal move (action {d.Action}) chosen at ply {ply} for {side}.");
                onMove?.Invoke(ply, side, board, d);
                referee.MakeMove(board, d.Move, side);
                ply++;
            }

            var end = new GameEnd { Plies = ply, Board = board };
            var terminal = referee.GetTerminalResult(board);
            if (terminal != TerminalResult.NotOver) {
                end.Terminal = true;
                end.ResultRedPov = terminal == TerminalResult.RedWins ? 1 : terminal == TerminalResult.BlueWins ? -1 : 0;
            } else {
                var (red, blue) = GetRedAndBlueCounts(board, PlayerColor.Red);
                end.ResultRedPov = red > blue ? 1 : blue > red ? -1 : 0;
            }
            return end;
        }

        public static AtaxxAIEngine NewReferee() => new AtaxxAIEngine(EngineParamsFile.LoadBase());   // rules only (the search is never called); built like every engine

        public static MoveDecision RandomDecision(AtaxxAIEngine referee, BitboardState board, PlayerColor side, Random rng) {
            var legal = referee.GetAllValidMoves(board, side);
            if (legal.Count == 0) return new MoveDecision { HasMove = false, Action = ActionCodec.NoAction };
            var m = legal[rng.Next(legal.Count)];
            return new MoveDecision { HasMove = true, Move = m, Action = ActionCodec.Encode(m), LegalCount = legal.Count, IsExploration = true };
        }
    }
}
