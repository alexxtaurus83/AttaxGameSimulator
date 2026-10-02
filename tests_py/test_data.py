import os
import random
import shutil
import struct
import sys
import tempfile
import unittest

import numpy as np
import torch

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import helpers as H  # noqa: E402
import ataxx_common as C  # noqa: E402
import ataxx_data as D  # noqa: E402

TEACHER = {"id": 1, "kind": 0, "depth": 3, "node_budget": 0, "root_bonus_scale": 1.0}


class LoaderTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="attax_py_")
        self.rng = random.Random(7)

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def p(self, name):
        return os.path.join(self.dir, name)

    def test_roundtrip_and_stats(self):
        games = [H.make_game(u, self.rng, n=5, result=1 if u % 2 else -1) for u in range(40)]
        H.write_log(self.p("a.bin"), games, [TEACHER])
        train, val = D.load_logs([self.p("a.bin")], val_fraction=0.25)
        self.assertEqual(train.stats["games"], 40)
        self.assertEqual(len(train) + len(val), 200)
        self.assertGreater(len(val), 0)
        self.assertEqual(train.stats["teacher_valid"], 40 * 3)   # plies 0,2,4 have teachers

    def test_split_is_by_game_and_never_leaks(self):
        games = [H.make_game(u, self.rng, n=4) for u in range(60)]
        H.write_log(self.p("a.bin"), games, [TEACHER])
        train, val = D.load_logs([self.p("a.bin")], val_fraction=0.3)
        self.assertFalse(set(train.a["uid"].tolist()) & set(val.a["uid"].tolist()))
        for u in set(val.a["uid"].tolist()):
            self.assertTrue(C.in_validation_split(u, 0.3))

    def test_symmetry_copies_of_one_game_stay_on_one_side(self):
        # Eight symmetry copies of the same positions share one uid inside one file: one side only.
        rng = random.Random(9)
        g = H.make_game(123456789, rng, n=4, teacher_every=0)
        g["samples"] = [s for s in g["samples"] for _ in range(8)]   # 8 copies of each position under one uid
        H.write_log(self.p("x.bin"), [g])
        for frac in (0.2, 0.5, 0.8):
            train, val = D.load_logs([self.p("x.bin")], val_fraction=frac)
            self.assertEqual(len(train) + len(val), 32)
            self.assertTrue((len(train) > 0) != (len(val) > 0), "a game's copies must not straddle train/val")
            self.assertEqual(len(val) > 0, C.in_validation_split(123456789, frac))

    def test_same_game_in_two_files_is_rejected(self):
        # A rerun with the same --seed/--generation, or a log loaded together with its relabeled copy, would double count.
        g = H.make_game(77, random.Random(9), n=4, teacher_every=0)
        H.write_log(self.p("x.bin"), [g])
        H.write_log(self.p("y.bin"), [g])
        with self.assertRaisesRegex(D.LogFormatError, "appears in both"):
            D.load_logs([self.p("x.bin"), self.p("y.bin")], val_fraction=0.0)

    def test_value_target_is_side_to_move_outcome(self):
        rng = random.Random(3)
        g = H.make_game(5, rng, n=6, result=1, teacher_every=0)
        H.write_log(self.p("a.bin"), [g])
        train, val = D.load_logs([self.p("a.bin")], val_fraction=0.0)
        a = train.a
        expect = np.where(a["side"] == 0, 1.0, -1.0)
        self.assertTrue(np.array_equal(a["value"], expect.astype(np.float32)))
        self.assertTrue(a["value_valid"].all())
        self.assertEqual(len(val), 0)

    def test_draw_gives_zero_value(self):
        g = H.make_game(6, random.Random(4), n=4, result=0, teacher_every=0)
        H.write_log(self.p("a.bin"), [g])
        train, _ = D.load_logs([self.p("a.bin")], val_fraction=0.0)
        self.assertTrue((train.a["value"] == 0).all())
        self.assertTrue(train.a["value_valid"].all())

    def test_plycap_outcome_excluded_by_default_but_policy_labels_kept(self):
        g = H.make_game(8, random.Random(5), n=6, result=1, termination=C.TERMINATION_PLY_CAP)
        H.write_log(self.p("a.bin"), [g], [TEACHER])
        train, _ = D.load_logs([self.p("a.bin")], val_fraction=0.0)
        self.assertFalse(train.a["value_valid"].any())
        self.assertTrue(train.a["teacher_valid"].any())
        train2, _ = D.load_logs([self.p("a.bin")], val_fraction=0.0, include_plycap=True)
        self.assertTrue(train2.a["value_valid"].all())

    def test_unplayed_teacher_action_never_inherits_trajectory_outcome(self):
        # Teacher label differs from the played action; the policy target must be the teacher action and the
        # value target must come only from the played trajectory's result (never from the teacher action).
        rng = random.Random(6)
        g = H.make_game(9, rng, n=6, result=-1, teacher_every=1)
        H.write_log(self.p("a.bin"), [g], [TEACHER])
        train, _ = D.load_logs([self.p("a.bin")], val_fraction=0.0)
        td = D.TensorData(train, torch.device("cpu"))
        b = td.batch(torch.arange(td.n))
        self.assertTrue(torch.equal(b["teacher"], td.teacher))
        # The fixture really does contain teacher actions that differ from the played ones.
        self.assertTrue(bool((b["teacher"] != b["played"]).any()))
        # Value is one number per position, equal to the played trajectory's outcome (result = -1 for red).
        self.assertEqual(b["value"].shape[0], td.n)
        expected = torch.where(td.side == 0, torch.tensor(-1.0), torch.tensor(1.0))
        self.assertTrue(torch.equal(b["value"], expected))

    def test_random_played_moves_are_not_policy_targets(self):
        g = H.make_game(10, random.Random(8), n=6, teacher_every=0)
        # mark all as random exploration
        g["samples"] = [(*s[:8], D.FLAG_PLAYED_RANDOM, 0, 0, 0) for s in g["samples"]]
        H.write_log(self.p("a.bin"), [g])
        train, _ = D.load_logs([self.p("a.bin")], val_fraction=0.0)
        self.assertEqual(int(train.a["teacher_valid"].sum()), 0)
        self.assertTrue(train.a["played_random"].all())

    def test_augmented_batch_keeps_targets_legal(self):
        rng = random.Random(11)
        games = [H.make_game(u, rng, n=8, teacher_every=1) for u in range(30)]
        H.write_log(self.p("a.bin"), games, [TEACHER])
        train, _ = D.load_logs([self.p("a.bin")], val_fraction=0.0)
        td = D.TensorData(train, torch.device("cpu"))
        gen = torch.Generator().manual_seed(1)
        for _ in range(5):
            b = td.batch(torch.randperm(td.n, generator=gen), augment=True, generator=gen)
            legal = C.legal_mask_from_planes(b["x"])
            self.assertTrue(legal.gather(1, b["played"][:, None]).all())
            tv = b["teacher_valid"]
            self.assertTrue(legal[tv].gather(1, b["teacher"][tv][:, None]).all())


class RejectionTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="attax_py_")
        self.rng = random.Random(21)

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def p(self, name):
        return os.path.join(self.dir, name)

    def load(self, name="a.bin"):
        return D.load_logs([self.p(name)], val_fraction=0.0)

    def test_wrong_version_and_magic(self):
        H.write_log(self.p("a.bin"), [H.make_game(1, self.rng)], [TEACHER], version=2)
        with self.assertRaisesRegex(D.LogFormatError, "version"):
            self.load()
        with open(self.p("b.bin"), "wb") as f:
            f.write(b"NOPE\x03\x00")
        with self.assertRaisesRegex(D.LogFormatError, "ATLG"):
            self.load("b.bin")

    def test_illegal_played_action(self):
        g = H.make_game(1, self.rng, n=3, teacher_every=0)
        s = list(g["samples"][0])
        red, blue, blocked, side = s[2], s[3], s[4], s[5]
        legal = C.reference_legal_actions(red, blue, blocked, side)
        s[6] = next(a for a in range(C.ACTION_COUNT) if a not in legal)
        g["samples"][0] = tuple(s)
        H.write_log(self.p("a.bin"), [g])
        with self.assertRaisesRegex(D.LogFormatError, "Illegal played action"):
            self.load()

    def test_illegal_teacher_action(self):
        g = H.make_game(1, self.rng, n=3, teacher_every=1)
        s = list(g["samples"][0])
        legal = C.reference_legal_actions(s[2], s[3], s[4], s[5])
        s[7] = next(a for a in range(C.ACTION_COUNT) if a not in legal)
        g["samples"][0] = tuple(s)
        H.write_log(self.p("a.bin"), [g], [TEACHER])
        with self.assertRaisesRegex(D.LogFormatError, "Illegal teacher action"):
            self.load()

    def test_teacher_flag_inconsistencies(self):
        g = H.make_game(1, self.rng, n=3, teacher_every=1)
        H.write_log(self.p("a.bin"), [g], [])  # teacher used but never declared
        with self.assertRaisesRegex(D.LogFormatError, "declared teacher"):
            self.load()
        g2 = H.make_game(2, self.rng, n=3, teacher_every=0)
        s = list(g2["samples"][0]); s[7] = 5                       # teacher action without valid flag
        g2["samples"][0] = tuple(s)
        H.write_log(self.p("b.bin"), [g2], [TEACHER])
        with self.assertRaisesRegex(D.LogFormatError, "TeacherValid is clear"):
            self.load("b.bin")

    def test_bad_board_data(self):
        for label, mutate in (
            ("overlap", lambda s: (s[0], s[1], s[2], s[2], s[4], *s[5:])),
            ("outside", lambda s: (s[0], s[1], s[2] | (1 << 55), s[3], s[4], *s[5:])),
        ):
            g = H.make_game(1, self.rng, n=2, teacher_every=0)
            g["samples"][0] = mutate(g["samples"][0])
            H.write_log(self.p(f"{label}.bin"), [g])
            with self.assertRaises(D.LogFormatError, msg=label):
                self.load(f"{label}.bin")

    def test_corruption_and_truncation(self):
        H.write_log(self.p("a.bin"), [H.make_game(1, self.rng, n=4)], [TEACHER])
        data = open(self.p("a.bin"), "rb").read()
        flipped = bytearray(data); flipped[len(data) - 20] ^= 0xFF
        open(self.p("c1.bin"), "wb").write(bytes(flipped))
        with self.assertRaises(Exception):
            self.load("c1.bin")
        open(self.p("c2.bin"), "wb").write(data[:-5])
        with self.assertRaises(Exception):
            self.load("c2.bin")
        open(self.p("c3.bin"), "wb").write(data[:-14])            # missing GameResult
        with self.assertRaisesRegex(D.LogFormatError, "missing GameResult|Unexpected end"):
            self.load("c3.bin")

    def test_uid_collision_between_different_games(self):
        H.write_log(self.p("a.bin"), [H.make_game(1, self.rng, seed=1)], [TEACHER])
        H.write_log(self.p("b.bin"), [H.make_game(1, self.rng, seed=2)], [TEACHER])
        with self.assertRaisesRegex(D.LogFormatError, "appears in both"):
            D.load_logs([self.p("a.bin"), self.p("b.bin")], val_fraction=0.0)

    def test_teacher_id_defined_differently_across_files(self):
        H.write_log(self.p("a.bin"), [H.make_game(1, self.rng)], [TEACHER])
        other = dict(TEACHER, depth=5)
        H.write_log(self.p("b.bin"), [H.make_game(2, self.rng)], [other])
        with self.assertRaisesRegex(D.LogFormatError, "defined differently"):
            D.load_logs([self.p("a.bin"), self.p("b.bin")], val_fraction=0.0)

    def test_empty_log(self):
        H.write_log(self.p("a.bin"), [{"uid": 1, "samples": [], "result": 0, "plies": 0, "termination": 0}])
        with self.assertRaisesRegex(D.LogFormatError, "no samples"):
            self.load()


if __name__ == "__main__":
    unittest.main()
