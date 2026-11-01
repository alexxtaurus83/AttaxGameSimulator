"""Test-only v3 log writer (independent re-implementation of Attax.Data.LogV3.LogV3Writer) and position helpers."""

import os
import random
import struct
import sys
import zlib

import lz4.block
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import ataxx_common as C  # noqa: E402
import ataxx_data as D  # noqa: E402


def sample_tuple(uid, ply, red, blue, blocked, side, played, teacher=-1, flags=0, sym=0, teacher_id=0, teacher_score=0):
    return (uid, ply, red, blue, blocked, side, played, teacher, flags, sym, teacher_id, teacher_score)


def write_log(path, games, teachers=(), version=4):
    """games: list of dict(uid, seed, generation, samples=[sample_tuple...], result, plies, termination)."""
    with open(path, "wb") as f:
        f.write(b"ATLG" + struct.pack("<H", version))
        for t in teachers:
            f.write(bytes([4]) + struct.pack("<BBBifBQ", t["id"], t.get("kind", 0), t.get("depth", 3), t.get("node_budget", 0),
                                             t.get("root_bonus_scale", 1.0), t.get("flags", 0), t.get("model_hash", 0)))
        for g in games:
            f.write(bytes([1]) + struct.pack("<QQBHBB", g["uid"], g.get("seed", 1), 7, g.get("generation", 0), g.get("red_mode", 0), g.get("blue_mode", 0)))
            samples = g["samples"]
            for start in range(0, len(samples), 64):
                chunk = samples[start:start + 64]
                arr = np.zeros(len(chunk), dtype=D.SAMPLE_DTYPE)
                for i, s in enumerate(chunk):
                    arr[i] = s
                raw = arr.tobytes()
                comp = lz4.block.compress(raw, store_size=False)
                f.write(bytes([2]) + struct.pack("<iiiI", len(chunk), len(raw), len(comp), zlib.crc32(comp) & 0xFFFFFFFF) + comp)
            f.write(bytes([3]) + struct.pack("<QbHB", g["uid"], g.get("result", 1), g.get("plies", len(samples) + 1), g.get("termination", 0)))


def random_position(rng, pieces=None, blocks=None):
    """Random (red, blue, blocked) python ints; side is chosen by the caller."""
    pieces = rng.randint(2, 30) if pieces is None else pieces
    blocks = rng.randint(0, 6) if blocks is None else blocks
    squares = list(range(49))
    rng.shuffle(squares)
    red = blue = blocked = 0
    i = 0
    for _ in range(blocks):
        blocked |= 1 << squares[i]
        i += 1
    for _ in range(pieces):
        if rng.random() < 0.5:
            red |= 1 << squares[i]
        else:
            blue |= 1 << squares[i]
        i += 1
    return red, blue, blocked


def legal_list(red, blue, blocked, side):
    return sorted(C.reference_legal_actions(red, blue, blocked, side))


def make_game(uid, rng, n=6, result=1, termination=0, teacher_every=2, teacher_id=1, generation=0, seed=None, scores=None):
    """A game whose played/teacher actions are legal in random positions (independent positions are fine for loader tests).
    scores: None = no teacher scores; a callable(ply) -> int gives a TeacherScore to every teacher-labelled sample."""
    samples = []
    for ply in range(n):
        while True:
            red, blue, blocked = random_position(rng)
            side = rng.randint(0, 1)
            legal = legal_list(red, blue, blocked, side)
            if legal:
                break
        played = rng.choice(legal)
        if teacher_every and ply % teacher_every == 0:
            if scores is None:
                samples.append(sample_tuple(uid, ply, red, blue, blocked, side, played, rng.choice(legal), D.FLAG_TEACHER_VALID, 0, teacher_id))
            else:
                samples.append(sample_tuple(uid, ply, red, blue, blocked, side, played, rng.choice(legal), D.FLAG_TEACHER_VALID | D.FLAG_TEACHER_SCORE_VALID, 0, teacher_id, int(scores(ply))))
        else:
            samples.append(sample_tuple(uid, ply, red, blue, blocked, side, played))
    return {"uid": uid, "seed": uid if seed is None else seed, "generation": generation, "samples": samples, "result": result, "plies": n + 1, "termination": termination}
