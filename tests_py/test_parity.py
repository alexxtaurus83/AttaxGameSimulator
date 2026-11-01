"""C#/Python contract parity.

Reads the JSON written by `Attax.Console contract-fixtures` (positions, C#-computed input planes, legal-action sets,
per-symmetry transformed boards and mapped actions, and optionally the ONNX model outputs computed by the C# runtime)
and checks that the Python implementation agrees exactly. Set ATTAX_FIXTURES to the fixture path; without it the
tests are skipped (run.ps1 generates the fixtures first).
"""

import json
import os
import sys
import unittest

import numpy as np
import torch

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import helpers  # noqa: E402,F401
import ataxx_common as C  # noqa: E402

FIXTURES = os.environ.get("ATTAX_FIXTURES")
MODEL = os.environ.get("ATTAX_MODEL")


def load():
    with open(FIXTURES) as f:
        return json.load(f)


def planes_of(p):
    t = lambda v: torch.tensor([v], dtype=torch.int64)
    return C.make_planes(t(p["red"]), t(p["blue"]), t(p["blocked"]), t(p["side"]))


@unittest.skipUnless(FIXTURES and os.path.exists(FIXTURES), "ATTAX_FIXTURES not set")
class ParityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = load()

    def test_action_count(self):
        self.assertEqual(self.data["action_count"], C.ACTION_COUNT)

    def test_input_planes_match_csharp_encoder(self):
        for p in self.data["positions"]:
            expected = np.array(p["planes"], dtype=np.float32).reshape(1, 4, 7, 7)
            self.assertTrue(np.array_equal(planes_of(p).numpy(), expected))

    def test_legal_actions_match_csharp(self):
        for p in self.data["positions"]:
            mask = C.legal_mask_from_planes(planes_of(p))[0]
            self.assertEqual(sorted(torch.nonzero(mask).flatten().tolist()), p["legal"])
            self.assertEqual(sorted(C.reference_legal_actions(p["red"], p["blue"], p["blocked"], p["side"])), p["legal"])

    def test_symmetry_boards_and_actions_match_csharp(self):
        for p in self.data["positions"]:
            for s, sym in enumerate(p["symmetries"]):
                x = C.apply_symmetry_to_planes(planes_of(p), torch.tensor([s]))
                self.assertTrue(torch.equal(x, planes_of({**p, "red": sym["red"], "blue": sym["blue"], "blocked": sym["blocked"]})))
                mapped = sorted(int(C.ACT_PERM[s, a]) for a in p["legal"])
                self.assertEqual(mapped, sym["legal_actions_mapped"])
                mask_t = C.legal_mask_from_planes(x)[0]
                self.assertEqual(sorted(torch.nonzero(mask_t).flatten().tolist()), mapped)

    @unittest.skipUnless(MODEL and os.path.exists(MODEL), "ATTAX_MODEL not set")
    def test_onnx_outputs_match_csharp_runtime(self):
        import onnxruntime as ort
        sess = ort.InferenceSession(MODEL, providers=["CPUExecutionProvider"])
        worst_v = worst_l = 0.0
        for p in self.data["positions"]:
            if "model_logits" not in p:
                self.skipTest("fixtures were written without --model")
            logits, value = sess.run([C.OUTPUT_POLICY, C.OUTPUT_VALUE], {C.INPUT_NAME: planes_of(p).numpy()})
            worst_v = max(worst_v, abs(float(value[0, 0]) - p["model_value"]))
            worst_l = max(worst_l, float(np.abs(logits[0] - np.array(p["model_logits"], dtype=np.float32)).max()))
        self.assertLess(worst_v, 1e-4)
        self.assertLess(worst_l, 1e-3)


if __name__ == "__main__":
    unittest.main()
