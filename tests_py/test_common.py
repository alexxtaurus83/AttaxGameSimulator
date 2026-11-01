import os
import random
import sys
import unittest

import numpy as np
import torch

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import helpers  # noqa: E402,F401
import ataxx_common as C  # noqa: E402


def to_planes(red, blue, blocked, side):
    return C.make_planes(torch.tensor([red], dtype=torch.int64), torch.tensor([blue], dtype=torch.int64),
                         torch.tensor([blocked], dtype=torch.int64), torch.tensor([side], dtype=torch.int64))


class ContractTests(unittest.TestCase):
    def test_constants(self):
        self.assertEqual(C.ACTION_COUNT, 833)
        self.assertEqual(len(C.JUMP_OFFSETS), 16)

    def test_planes_perspective(self):
        red, blue, blocked = 1 << 0, 1 << 1, 1 << 7
        xr = to_planes(red, blue, blocked, 0)[0]
        xb = to_planes(red, blue, blocked, 1)[0]
        self.assertEqual(xr[0, 0, 0].item(), 1.0)   # friendly = red
        self.assertEqual(xr[1, 0, 1].item(), 1.0)   # enemy = blue
        self.assertEqual(xb[0, 0, 1].item(), 1.0)   # friendly = blue
        self.assertEqual(xb[1, 0, 0].item(), 1.0)
        self.assertEqual(xr[2, 1, 0].item(), 1.0)   # blocked square 7 -> (x=0, y=1)
        self.assertTrue(torch.all(xr[3] == 1.0))

    def test_legal_mask_matches_reference(self):
        rng = random.Random(1)
        for _ in range(300):
            red, blue, blocked = helpers.random_position(rng)
            side = rng.randint(0, 1)
            mask = C.legal_mask_from_planes(to_planes(red, blue, blocked, side))[0]
            got = set(torch.nonzero(mask).flatten().tolist())
            self.assertEqual(got, C.reference_legal_actions(red, blue, blocked, side))

    def test_action_permutations_are_bijections_and_invertible(self):
        for s in range(8):
            self.assertEqual(len(set(C.ACT_PERM[s].tolist())), C.ACTION_COUNT)
        inv = {}
        for s in range(8):
            for t in range(8):
                if all(C.SQ_PERM[t, C.SQ_PERM[s, q]] == q for q in range(49)):
                    inv[s] = t
        self.assertEqual(len(inv), 8)
        for s in range(8):
            a = np.arange(C.ACTION_COUNT)
            self.assertTrue(np.array_equal(C.ACT_PERM[inv[s], C.ACT_PERM[s, a]], a))

    def test_symmetry_commutes_with_legality(self):
        rng = random.Random(2)
        for _ in range(60):
            red, blue, blocked = helpers.random_position(rng)
            side = rng.randint(0, 1)
            x = to_planes(red, blue, blocked, side)
            mask = C.legal_mask_from_planes(x)[0]
            for s in range(8):
                xt = C.apply_symmetry_to_planes(x, torch.tensor([s]))
                maskt = C.legal_mask_from_planes(xt)[0]
                for a in torch.nonzero(mask).flatten().tolist():
                    self.assertTrue(bool(maskt[C.ACT_PERM[s, a]]))
                self.assertEqual(int(mask.sum()), int(maskt.sum()))

    def test_symmetry_planes_match_bitboard_transform(self):
        rng = random.Random(3)
        for _ in range(40):
            red, blue, blocked = helpers.random_position(rng)
            x = to_planes(red, blue, blocked, 0)
            for s in range(8):
                def tb(bits):
                    out = 0
                    for q in range(49):
                        if (bits >> q) & 1:
                            out |= 1 << int(C.SQ_PERM[s, q])
                    return out
                expected = to_planes(tb(red), tb(blue), tb(blocked), 0)
                got = C.apply_symmetry_to_planes(x, torch.tensor([s]))
                self.assertTrue(torch.equal(expected, got))

    def test_transform_actions_matches_table(self):
        a = torch.arange(C.ACTION_COUNT)
        for s in range(8):
            self.assertTrue(torch.equal(C.transform_actions(a, torch.full_like(a, s)), torch.from_numpy(C.ACT_PERM[s])))

    def test_jump_source(self):
        self.assertEqual(C.jump_source(49 + 24), 24 + C.JUMP_OFFSETS[0][1] * 7 + C.JUMP_OFFSETS[0][0])
        with self.assertRaises(ValueError):
            C.jump_source(5)

    def test_uid_hash_golden_values(self):
        # Must equal the constants asserted in Attax.Core.Tests/LogV3Tests.cs (computed with zlib).
        self.assertEqual(C.uid_hash(0), 0x6522DF69)
        self.assertEqual(C.uid_hash(1), 0xA988DFF7)
        self.assertEqual(C.uid_hash(0x0123456789ABCDEF), 0x443BE247)
        self.assertFalse(C.in_validation_split(5, 0.0))
        self.assertTrue(C.in_validation_split(5, 1.0))


if __name__ == "__main__":
    unittest.main()
