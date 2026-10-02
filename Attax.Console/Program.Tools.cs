using System.Diagnostics;
using System.Globalization;
using System.Text;
using Attax.Core;
using Attax.Model;
using Attax.Eval.OnnxRuntime;
using Attax.Play;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Console {
    partial class Program {
        static void RunVerifyModel(VerifyModelOptions o) {
            System.Console.WriteLine(OnnxPolicyValueModel.GetRuntimeDiagnostics());
            using var model = new OnnxPolicyValueModel(o.Model, o.Threads);   // throws with a precise message if the contract is broken
            System.Console.WriteLine($"[verify] contract OK: {o.Model}");
            foreach (var kv in model.Metadata.OrderBy(k => k.Key)) System.Console.WriteLine($"[verify]   {kv.Key} = {kv.Value}");

            var rng = new Random(1);
            var referee = NewReferee();
            var boards = new List<BitboardState>();
            var b = CreateStandardInitialBoard();
            var side = PlayerColor.Red;
            for (int ply = 0; ply < 60 && !referee.IsGameOver(b); ply++) {
                boards.Add(b.Clone());
                var d = RandomDecision(referee, b, side, rng);
                referee.MakeMove(b, d.Move, side);
                side = SwitchPlayer(side);
            }

            // Output sanity on real positions (from the side to move at each ply).
            int checkedPositions = 0;
            for (int i = 0; i < boards.Count; i++) {
                var s = i % 2 == 0 ? PlayerColor.Red : PlayerColor.Blue;
                var one = model.Evaluate(new[] { boards[i] }, s);   // throws on non-finite or out-of-range output
                var mask = new bool[ActionCodec.ActionCount];
                if (ActionCodec.FillLegalMask(boards[i], s, mask) == 0) continue;
                checkedPositions++;
            }
            System.Console.WriteLine($"[verify] {checkedPositions} positions evaluated, all outputs finite and value in [-1,1]");

            // A position evaluated alone must equal the same position inside a batch.
            var batchOut = model.Evaluate(boards, PlayerColor.Red);
            var singleOut = model.Evaluate(new[] { boards[0] }, PlayerColor.Red);
            float dv = Math.Abs(batchOut.Values[0] - singleOut.Values[0]);
            float dl = 0; for (int a = 0; a < ActionCodec.ActionCount; a++) dl = Math.Max(dl, Math.Abs(batchOut.Logits[a] - singleOut.Logits[a]));
            System.Console.WriteLine($"[verify] batch-vs-single max diff: value={dv:0.0e+0}, policy={dl:0.0e+0}");
            if (dv > 1e-4f || dl > 1e-3f) throw new InvalidOperationException("Model output depends on batch size.");

            // Latency.
            foreach (int n in new[] { 1, 8, 32 }) {
                var set = Enumerable.Range(0, n).Select(i => boards[i % boards.Count]).ToList();
                for (int w = 0; w < 20; w++) model.Evaluate(set, PlayerColor.Red);
                int reps = n == 1 ? 400 : 100;
                var sw = Stopwatch.StartNew();
                for (int r = 0; r < reps; r++) model.Evaluate(set, PlayerColor.Red);
                sw.Stop();
                System.Console.WriteLine($"[verify] latency batch={n}: {sw.Elapsed.TotalMilliseconds / reps:0.000} ms/call, {sw.Elapsed.TotalMilliseconds / reps / n:0.000} ms/position (threads={(o.Threads == 0 ? "default" : o.Threads.ToString())}, CPU)");
            }
        }

        /// <summary>
        /// Writes positions with everything the Python side must agree on: input planes, legal-action set, per-symmetry transformed
        /// boards and actions, and (optionally) the ONNX model's outputs computed by the C# runtime.
        /// </summary>
        static void RunFixtures(FixturesOptions o) {
            var rng = new Random(o.Seed);
            var referee = NewReferee();
            var inv = CultureInfo.InvariantCulture;
            OnnxPolicyValueModel? model = o.Model == null ? null : new OnnxPolicyValueModel(o.Model, 1);

            var positions = new List<(BitboardState board, PlayerColor side)>();
            while (positions.Count < o.Count) {
                var b = CreateStandardInitialBoard();
                // random blocks so the blocked plane is exercised
                int blocks = rng.Next(0, 5);
                var empty = Enumerable.Range(0, 49).Where(i => ((b.RedPieces | b.BluePieces) >> i & 1UL) == 0).OrderBy(_ => rng.Next()).Take(blocks);
                foreach (int sq in empty) b.BlockedSquares |= 1UL << sq;
                var side = PlayerColor.Red;
                int plies = rng.Next(0, 70);
                for (int ply = 0; ply < plies && !referee.IsGameOver(b); ply++) {
                    var d = RandomDecision(referee, b, side, rng);
                    if (!d.HasMove) break;
                    referee.MakeMove(b, d.Move, side);
                    side = SwitchPlayer(side);
                }
                if (referee.IsGameOver(b)) continue;
                positions.Add((b, side));
            }

            var sb = new StringBuilder();
            sb.Append("{\"action_count\":").Append(ActionCodec.ActionCount).Append(",\"positions\":[");
            for (int i = 0; i < positions.Count; i++) {
                var (b, side) = positions[i];
                if (i > 0) sb.Append(',');
                var mask = new bool[ActionCodec.ActionCount];
                ActionCodec.FillLegalMask(b, side, mask);
                var planes = new float[BoardEncoder.FloatsPerBoard];
                BoardEncoder.Encode(b, side, planes, 0);
                sb.Append("{\"red\":").Append(b.RedPieces.ToString(inv))
                  .Append(",\"blue\":").Append(b.BluePieces.ToString(inv))
                  .Append(",\"blocked\":").Append(b.BlockedSquares.ToString(inv))
                  .Append(",\"side\":").Append(side == PlayerColor.Red ? 0 : 1)
                  .Append(",\"legal\":[").Append(string.Join(",", Enumerable.Range(0, ActionCodec.ActionCount).Where(a => mask[a]))).Append(']')
                  .Append(",\"planes\":[").Append(string.Join(",", planes.Select(f => f.ToString("0", inv)))).Append(']');

                sb.Append(",\"symmetries\":[");
                for (int s = 0; s < 8; s++) {
                    if (s > 0) sb.Append(',');
                    var t = ApplySymmetryToBoard(b, s);
                    var tm = new bool[ActionCodec.ActionCount];
                    ActionCodec.FillLegalMask(t, side, tm);
                    sb.Append("{\"red\":").Append(t.RedPieces.ToString(inv)).Append(",\"blue\":").Append(t.BluePieces.ToString(inv))
                      .Append(",\"blocked\":").Append(t.BlockedSquares.ToString(inv))
                      .Append(",\"legal_actions_mapped\":[").Append(string.Join(",", Enumerable.Range(0, ActionCodec.ActionCount).Where(a => mask[a]).Select(a => ActionCodec.TransformAction(a, s)).OrderBy(a => a))).Append("]}");
                }
                sb.Append(']');

                if (model != null) {
                    var r = model.Evaluate(new[] { b }, side);
                    sb.Append(",\"model_value\":").Append(r.Values[0].ToString("R", inv))
                      .Append(",\"model_logits\":[").Append(string.Join(",", r.Logits.Select(f => f.ToString("R", inv)))).Append(']');
                }
                sb.Append('}');
            }
            sb.Append("]}");
            File.WriteAllText(o.Out, sb.ToString(), new UTF8Encoding(false));
            model?.Dispose();
            System.Console.WriteLine($"[fixtures] wrote {positions.Count} positions to {o.Out}{(o.Model != null ? " (with model outputs)" : "")}");
        }
    }
}
