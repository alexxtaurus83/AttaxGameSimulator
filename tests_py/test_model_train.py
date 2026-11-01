import json
import math
import os
import random
import shutil
import sys
import tempfile
import unittest

import numpy as np
import torch

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import helpers as H  # noqa: E402
import ataxx_common as C  # noqa: E402
import ataxx_data as D  # noqa: E402
import ataxx_model as M  # noqa: E402
import train as T  # noqa: E402

TEACHER = {"id": 1, "kind": 0, "depth": 3, "node_budget": 0, "root_bonus_scale": 1.0}


def batch_of(n, rng, teacher_frac=1.0, value_frac=1.0):
    reds, blues, blockeds, sides = [], [], [], []
    played, teacher = [], []
    for _ in range(n):
        while True:
            r, b, k = H.random_position(rng)
            s = rng.randint(0, 1)
            legal = H.legal_list(r, b, k, s)
            if legal:
                break
        reds.append(r); blues.append(b); blockeds.append(k); sides.append(s)
        played.append(rng.choice(legal)); teacher.append(rng.choice(legal))
    t = lambda v: torch.tensor(v, dtype=torch.int64)
    x = C.make_planes(t(reds), t(blues), t(blockeds), t(sides))
    tv = torch.tensor([rng.random() < teacher_frac for _ in range(n)])
    vv = torch.tensor([rng.random() < value_frac for _ in range(n)])
    return {"x": x, "teacher": t(teacher), "teacher_valid": tv, "played": t(played),
            "value": torch.tensor([rng.choice([-1.0, 0.0, 1.0]) for _ in range(n)]), "value_valid": vv}


class ValueTargetTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="attax_vt_")
        self.rng = random.Random(5)

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def load(self, games, **kw):
        path = os.path.join(self.dir, "a.bin")
        H.write_log(path, games, [TEACHER])
        train, _ = D.load_logs([path], val_fraction=0.0, **kw)
        return train

    def test_score_target_is_tanh_of_scaled_score_and_saturates_on_decisive_results(self):
        scores = {0: 0, 1: 1_500_000, 2: -1_500_000, 3: 7_000_000, 4: 999_999_000, 5: -999_999_000}
        g = H.make_game(1, self.rng, n=6, teacher_every=1, scores=lambda ply: scores[ply])
        td = D.TensorData(self.load([g]), torch.device("cpu"), value_mode="score", score_scale=4.0e6)
        expected = [math.tanh(scores[i] / 4.0e6) for i in range(6)]
        self.assertTrue(torch.allclose(td.value, torch.tensor(expected, dtype=torch.float32), atol=1e-6))
        self.assertEqual(td.value[4].item(), 1.0)
        self.assertEqual(td.value[5].item(), -1.0)
        self.assertTrue(bool(td.value_valid.all()))
        self.assertLess(td.value[3].item(), 0.95)         # an ordinary strong position is NOT squashed to +-1

    def test_score_is_side_to_move_value_so_no_perspective_flip_is_applied(self):
        g = H.make_game(2, self.rng, n=8, teacher_every=1, result=1, scores=lambda ply: 2_000_000)
        td = D.TensorData(self.load([g]), torch.device("cpu"), value_mode="score")
        self.assertTrue(torch.allclose(td.value, torch.full((8,), math.tanh(0.5)), atol=1e-6))   # same sign for red and blue to move
        out = D.TensorData(self.load([g]), torch.device("cpu"), value_mode="outcome")
        self.assertTrue(bool((out.value == torch.where(out.side == 0, 1.0, -1.0)).all()))         # the outcome DOES flip with the side

    def test_rows_without_a_score_get_no_value_target_and_never_fall_back_to_the_outcome(self):
        g = H.make_game(3, self.rng, n=8, teacher_every=2, scores=lambda ply: 1_000_000)          # teacher labels (with score) on even plies only
        g["samples"][1] = H.sample_tuple(*g["samples"][1][:7], g["samples"][1][6], D.FLAG_TEACHER_VALID, 0, 1, 0)   # a labelled row WITHOUT a score (teacher = played action, legal)
        td = D.TensorData(self.load([g]), torch.device("cpu"), value_mode="score")
        self.assertEqual(int(td.value_valid.sum()), 4)                                              # plies 0, 2, 4, 6 carry a score; ply 1 is labelled without one
        self.assertFalse(bool(td.value_valid[1]))
        self.assertTrue(bool(td.teacher_valid[1]))                                                  # it still trains the policy
        self.assertEqual(int(D.TensorData(self.load([g]), torch.device("cpu"), value_mode="outcome").value_valid.sum()), 8)

    def test_mix_needs_both_targets_and_is_a_convex_combination(self):
        g = H.make_game(4, self.rng, n=6, teacher_every=1, result=-1, scores=lambda ply: 4_000_000)
        ds = self.load([g])
        td = D.TensorData(ds, torch.device("cpu"), value_mode="mix", value_mix=0.25)
        s = math.tanh(1.0)
        expect = torch.where(td.side == 0, 0.25 * s + 0.75 * -1.0, 0.25 * s + 0.75 * 1.0)
        self.assertTrue(torch.allclose(td.value, expect.float(), atol=1e-6))
        capped = H.make_game(5, self.rng, n=6, teacher_every=1, termination=C.TERMINATION_PLY_CAP, scores=lambda ply: 4_000_000)
        td2 = D.TensorData(self.load([capped]), torch.device("cpu"), value_mode="mix")
        self.assertEqual(int(td2.value_valid.sum()), 0)                                             # ply-cap outcome is not real, so no mix row
        td3 = D.TensorData(self.load([capped]), torch.device("cpu"), value_mode="score")
        self.assertEqual(int(td3.value_valid.sum()), 6)                                             # but the score does not depend on the outcome

    def test_symmetry_does_not_change_the_score_target(self):
        g = H.make_game(6, self.rng, n=8, teacher_every=1, scores=lambda ply: 1_000_000 * ply)
        td = D.TensorData(self.load([g]), torch.device("cpu"), value_mode="score")
        gen = torch.Generator().manual_seed(0)
        idx = torch.arange(td.n)
        plain = td.batch(idx)["value"]; aug = td.batch(idx, augment=True, generator=gen)["value"]
        self.assertTrue(torch.equal(plain, aug))

    def test_unlabeled_rows_are_dropped_and_the_rest_is_untouched(self):
        g = H.make_game(11, self.rng, n=10, teacher_every=3, scores=lambda ply: 1_000_000 * ply)     # labelled on plies 0, 3, 6, 9
        td = D.TensorData(self.load([g]), torch.device("cpu"), value_mode="score")
        self.assertEqual(td.n, 10)
        kept, dropped = td.drop_untargeted()
        self.assertEqual((kept.n, dropped), (4, 6))
        self.assertTrue(bool(kept.teacher_valid.all()) and bool(kept.value_valid.all()))
        self.assertTrue(torch.equal(kept.value, td.value[td.teacher_valid | td.value_valid]))
        again, d2 = kept.drop_untargeted()
        self.assertEqual(d2, 0); self.assertIs(again, kept)
        b = kept.batch(torch.arange(kept.n))                                                            # still produces a full batch
        self.assertEqual(tuple(b["x"].shape), (4, 4, 7, 7))
        self.assertEqual(td.n, 10)                                                                      # the original is not modified

    def test_outcome_mode_keeps_unlabeled_rows_that_still_have_an_outcome(self):
        g = H.make_game(12, self.rng, n=10, teacher_every=3, result=1, scores=lambda ply: 1)
        td = D.TensorData(self.load([g]), torch.device("cpu"), value_mode="outcome")
        kept, dropped = td.drop_untargeted()
        self.assertEqual(dropped, 0)                                                                    # every row has an outcome target

    def test_bad_arguments_are_rejected(self):
        g = H.make_game(7, self.rng, n=4, teacher_every=1, scores=lambda ply: 1)
        ds = self.load([g])
        with self.assertRaises(ValueError):
            D.TensorData(ds, torch.device("cpu"), value_mode="nonsense")
        with self.assertRaises(ValueError):
            D.TensorData(ds, torch.device("cpu"), value_mode="score", score_scale=0)
        with self.assertRaises(ValueError):
            D.TensorData(ds, torch.device("cpu"), value_mode="mix", value_mix=1.5)

    def test_loader_rejects_inconsistent_score_fields(self):
        g = H.make_game(8, self.rng, n=3, teacher_every=1)
        s = list(g["samples"][0]); s[11] = 5                       # score value without the flag
        g["samples"][0] = tuple(s)
        H.write_log(os.path.join(self.dir, "b.bin"), [g], [TEACHER])
        with self.assertRaisesRegex(D.LogFormatError, "TeacherScoreValid is clear"):
            D.load_logs([os.path.join(self.dir, "b.bin")], val_fraction=0.0)
        g2 = H.make_game(9, self.rng, n=3, teacher_every=0)
        s = list(g2["samples"][0]); s[8] = D.FLAG_TEACHER_SCORE_VALID   # score flag without a teacher label
        g2["samples"][0] = tuple(s)
        H.write_log(os.path.join(self.dir, "c.bin"), [g2])
        with self.assertRaisesRegex(D.LogFormatError, "score flag without a valid teacher"):
            D.load_logs([os.path.join(self.dir, "c.bin")], val_fraction=0.0)

    def test_version_3_logs_are_rejected(self):
        g = H.make_game(10, self.rng, n=3)
        H.write_log(os.path.join(self.dir, "v3.bin"), [g], [TEACHER], version=3)
        with self.assertRaisesRegex(D.LogFormatError, "version 3"):
            D.load_logs([os.path.join(self.dir, "v3.bin")], val_fraction=0.0)


class ValueMetricTests(unittest.TestCase):
    def test_r2_is_one_for_perfect_zero_for_the_mean_and_negative_for_worse(self):
        rng = random.Random(1)
        n = 64
        x = H.random_position  # silence linter
        reds, blues, blockeds, sides = [], [], [], []
        for _ in range(n):
            r, b, k = H.random_position(rng); reds.append(r); blues.append(b); blockeds.append(k); sides.append(rng.randint(0, 1))
        t = lambda v: torch.tensor(v, dtype=torch.int64)
        batch = {"x": C.make_planes(t(reds), t(blues), t(blockeds), t(sides)), "teacher": torch.zeros(n, dtype=torch.int64),
                 "teacher_valid": torch.zeros(n, dtype=torch.bool), "played": torch.zeros(n, dtype=torch.int64),
                 "value": torch.linspace(-0.8, 0.8, n), "value_valid": torch.ones(n, dtype=torch.bool)}
        logits = torch.zeros(n, C.ACTION_COUNT)
        _, m = M.compute_losses((logits, batch["value"][:, None].clone()), batch)
        self.assertAlmostEqual(m["value_r2"], 1.0, places=5)
        mean_pred = torch.full((n, 1), float(batch["value"].mean()))
        _, m = M.compute_losses((logits, mean_pred), batch)
        self.assertAlmostEqual(m["value_r2"], 0.0, places=5)
        _, m = M.compute_losses((logits, -batch["value"][:, None].clone()), batch)
        self.assertLess(m["value_r2"], -0.5)
        self.assertAlmostEqual(m["value_sign_acc_ordinary"], 0.0, places=5)

class ModelTests(unittest.TestCase):
    def test_output_shapes_and_ranges(self):
        net = M.AtaxxPolicyValueNet(channels=16, layers=3)
        x = batch_of(5, random.Random(1))["x"]
        logits, value = net(x)
        self.assertEqual(tuple(logits.shape), (5, C.ACTION_COUNT))
        self.assertEqual(tuple(value.shape), (5, 1))
        self.assertLessEqual(value.abs().max().item(), 1.0)

    def test_policy_layout_is_kind_major(self):
        # The flattened [17,7,7] head must index as kind*49 + square.
        net = M.AtaxxPolicyValueNet(channels=8, layers=2)
        x = batch_of(1, random.Random(2))["x"]
        with torch.no_grad():
            feats = x
            for conv in net.convs:
                feats = torch.relu(conv(feats))
            head = net.policy_conv(feats)                   # [1, 17, 7, 7]
            logits, _ = net(x)
        self.assertTrue(torch.allclose(logits.reshape(1, 17, 7, 7), head, atol=1e-6))
        self.assertAlmostEqual(logits[0, 3 * 49 + 5 * 7 + 2].item(), head[0, 3, 5, 2].item(), places=6)

    def test_illegal_logits_are_masked(self):
        net = M.AtaxxPolicyValueNet(channels=8, layers=2)
        b = batch_of(8, random.Random(3))
        logits, _ = net(b["x"])
        masked, legal = M.masked_logits(logits * 0 + 1e3, b["x"])   # huge logits everywhere
        self.assertTrue(torch.all(masked[~legal] <= M.MASK_FILL + 1))
        self.assertTrue(torch.all(masked[legal] == 1e3))
        picked = masked.argmax(1)
        self.assertTrue(legal.gather(1, picked[:, None]).all())

    def test_loss_uses_only_valid_targets(self):
        torch.manual_seed(0)
        net = M.AtaxxPolicyValueNet(channels=8, layers=2)
        b = batch_of(32, random.Random(4), teacher_frac=0.5, value_frac=0.5)
        out = net(b["x"])
        total, m = M.compute_losses(out, b)
        self.assertEqual(m["n_policy"], int(b["teacher_valid"].sum()))
        self.assertEqual(m["n_value"], int(b["value_valid"].sum()))

        # Changing targets on invalid rows must not change the loss.
        b2 = {k: v.clone() for k, v in b.items()}
        b2["teacher"][~b2["teacher_valid"]] = 0
        b2["value"][~b2["value_valid"]] = 123.0
        total2, _ = M.compute_losses(out, b2)
        self.assertAlmostEqual(total.item(), total2.item(), places=6)

    def test_loss_with_no_valid_rows_is_finite_and_differentiable(self):
        net = M.AtaxxPolicyValueNet(channels=8, layers=2)
        b = batch_of(8, random.Random(5), teacher_frac=0.0, value_frac=0.0)
        total, m = M.compute_losses(net(b["x"]), b)
        self.assertEqual(total.item(), 0.0)
        total.backward()

    def test_policy_loss_is_cross_entropy_over_legal_moves_only(self):
        net = M.AtaxxPolicyValueNet(channels=8, layers=2)
        b = batch_of(6, random.Random(6), teacher_frac=1.0, value_frac=0.0)
        logits, value = net(b["x"])
        _, m = M.compute_losses((logits, value), b)
        masked, legal = M.masked_logits(logits, b["x"])
        expected = torch.nn.functional.cross_entropy(masked, b["teacher"]).item()
        self.assertAlmostEqual(m["policy_loss"], expected, places=5)
        # uniform logits over k legal moves => loss == log(k) on average
        uniform = torch.zeros_like(logits)
        _, m2 = M.compute_losses((uniform, value), b)
        expected_uniform = float(torch.log(legal.sum(1).float()).mean())
        self.assertAlmostEqual(m2["policy_loss"], expected_uniform, places=4)

    def test_value_sign_accuracy_ignores_draws(self):
        net = M.AtaxxPolicyValueNet(channels=8, layers=2)
        b = batch_of(6, random.Random(7), teacher_frac=0.0, value_frac=1.0)
        b["value"] = torch.tensor([1.0, -1.0, 0.0, 1.0, -1.0, 0.0])
        value = torch.tensor([[0.5], [-0.2], [0.9], [-0.3], [-0.9], [0.1]])
        _, m = M.compute_losses((torch.zeros(6, C.ACTION_COUNT), value), b)
        self.assertAlmostEqual(m["value_sign_acc"], 3 / 4, places=6)


class LrScheduleTests(unittest.TestCase):
    def test_constant(self):
        for p in (0.0, 0.5, 1.0):
            self.assertEqual(T.lr_at(1e-3, "constant", 0.02, p), 1e-3)

    def test_cosine_endpoints_monotonic_and_clamped(self):
        self.assertAlmostEqual(T.lr_at(1e-3, "cosine", 0.02, 0.0), 1e-3)
        self.assertAlmostEqual(T.lr_at(1e-3, "cosine", 0.02, 1.0), 2e-5)
        self.assertAlmostEqual(T.lr_at(1e-3, "cosine", 0.02, 0.5), 1e-3 * (0.02 + 0.98 * 0.5))
        vals = [T.lr_at(1e-3, "cosine", 0.02, i / 100) for i in range(101)]
        self.assertTrue(all(a >= b for a, b in zip(vals, vals[1:])))
        self.assertEqual(T.lr_at(1e-3, "cosine", 0.02, 7.0), T.lr_at(1e-3, "cosine", 0.02, 1.0))
        self.assertEqual(T.lr_at(1e-3, "cosine", 0.02, -3.0), 1e-3)


class OnnxTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="attax_onnx_")

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def test_export_contract_and_parity(self):
        torch.manual_seed(1)
        net = M.AtaxxPolicyValueNet(channels=16, layers=3)
        path = os.path.join(self.dir, "m.onnx")
        M.export_onnx(net, path, {"note": "test"})
        res = M.validate_onnx(net, path)
        self.assertLess(res["max_abs_diff_policy"], 1e-4)
        import onnx
        meta = {p.key: p.value for p in onnx.load(path).metadata_props}
        self.assertEqual(meta["contract"], C.CONTRACT_ID)
        self.assertEqual(meta["action_count"], "833")
        self.assertEqual(json.loads(meta["arch"]), net.config)

    def test_validation_rejects_a_model_that_does_not_match(self):
        torch.manual_seed(2)
        net = M.AtaxxPolicyValueNet(channels=16, layers=3)
        other = M.AtaxxPolicyValueNet(channels=16, layers=3)
        path = os.path.join(self.dir, "m.onnx")
        M.export_onnx(net, path)
        with self.assertRaisesRegex(AssertionError, "mismatch"):
            M.validate_onnx(other, path)

    def test_validation_rejects_wrong_output_names(self):
        import onnx
        net = M.AtaxxPolicyValueNet(channels=8, layers=2)
        path = os.path.join(self.dir, "m.onnx")
        M.export_onnx(net, path)
        m = onnx.load(path)
        for o in m.graph.output:
            if o.name == C.OUTPUT_VALUE:
                for node in m.graph.node:
                    for i, n in enumerate(node.output):
                        if n == C.OUTPUT_VALUE:
                            node.output[i] = "renamed"
                o.name = "renamed"
        onnx.save(m, path)
        with self.assertRaises(AssertionError):
            M.validate_onnx(net, path)


class TrainEndToEnd(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="attax_train_")

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def _data(self, n_games=60):
        rng = random.Random(33)
        games = [H.make_game(u, rng, n=8, result=rng.choice([-1, 0, 1]), scores=lambda ply: (ply - 4) * 1_500_000) for u in range(n_games)]
        H.write_log(os.path.join(self.dir, "d.bin"), games, [TEACHER])
        return os.path.join(self.dir, "d.bin")

    def test_train_exports_valid_onnx_and_report(self):
        data = self._data()
        out = os.path.join(self.dir, "gen1")
        rep = T.main(["--data", data, "--out", out, "--epochs", "2", "--batch-size", "64", "--channels", "16",
                      "--layers", "3", "--device", "cpu", "--val-fraction", "0.2"])
        self.assertTrue(os.path.exists(os.path.join(out, "model.onnx")))
        self.assertTrue(os.path.exists(os.path.join(out, "checkpoint.pt")))
        self.assertEqual(len(rep["history"]), 2)
        self.assertIsNotNone(rep["onnx_parity"])
        saved = json.load(open(os.path.join(out, "report.json")))
        self.assertEqual(saved["onnx_sha256"], rep["onnx_sha256"])

    def test_refuses_non_empty_out_without_flag(self):
        data = self._data(10)
        out = os.path.join(self.dir, "gen1")
        os.makedirs(out)
        open(os.path.join(out, "x"), "w").close()
        with self.assertRaises(SystemExit):
            T.main(["--data", data, "--out", out, "--epochs", "1", "--device", "cpu"])

    def test_resume_continues_and_matches_uninterrupted_epoch_count(self):
        data = self._data(20)
        out = os.path.join(self.dir, "gen1")
        common = ["--data", data, "--out", out, "--batch-size", "64", "--channels", "8", "--layers", "2",
                  "--device", "cpu", "--val-fraction", "0.2", "--no-validate-onnx"]
        T.main(common + ["--epochs", "1"])
        rep = T.main(common + ["--epochs", "3", "--resume"])
        self.assertEqual([h["epoch"] for h in rep["history"]], [1, 2, 3])

    def test_resume_requires_checkpoint_and_same_architecture(self):
        data = self._data(10)
        out = os.path.join(self.dir, "gen1")
        with self.assertRaises(SystemExit):
            T.main(["--data", data, "--out", out, "--epochs", "1", "--device", "cpu", "--resume"])
        common = ["--data", data, "--out", out, "--batch-size", "64", "--device", "cpu", "--no-validate-onnx"]
        T.main(common + ["--epochs", "1", "--channels", "8", "--layers", "2"])
        with self.assertRaises(SystemExit):
            T.main(common + ["--epochs", "2", "--channels", "16", "--layers", "2", "--resume"])

    def test_overfit_sanity_reduces_policy_loss(self):
        rng = random.Random(1)
        games = [H.make_game(u, rng, n=8, teacher_every=1, scores=lambda ply: (ply - 4) * 1_500_000) for u in range(10)]
        H.write_log(os.path.join(self.dir, "o.bin"), games, [TEACHER])
        out = os.path.join(self.dir, "ov")
        rep = T.main(["--data", os.path.join(self.dir, "o.bin"), "--out", out, "--epochs", "60", "--batch-size", "80",
                      "--channels", "32", "--layers", "3", "--device", "cpu", "--val-fraction", "0", "--lr", "3e-3",
                      "--no-augment", "--no-validate-onnx", "--overfit-n", "80"])
        first, last = rep["history"][0]["train_policy_loss"], rep["history"][-1]["train_policy_loss"]
        self.assertLess(last, first * 0.5)

    def test_fails_when_no_targets(self):
        rng = random.Random(2)
        g = H.make_game(1, rng, n=4, teacher_every=0, termination=C.TERMINATION_PLY_CAP)
        H.write_log(os.path.join(self.dir, "n.bin"), [g])
        with self.assertRaises(SystemExit):
            T.main(["--data", os.path.join(self.dir, "n.bin"), "--out", os.path.join(self.dir, "no"), "--epochs", "1",
                    "--device", "cpu", "--val-fraction", "0"])


if __name__ == "__main__":
    unittest.main()
