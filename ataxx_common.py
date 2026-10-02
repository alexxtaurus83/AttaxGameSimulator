"""Shared contract for the Ataxx policy/value pipeline.

Everything here mirrors the C# side (Attax.Core.ActionCodec, BoardEncoder, BitboardOps symmetries) and is
cross-checked by tests_py/test_common.py against fixtures emitted by the C# `contract-fixtures` command.

Board:   7x7, square index = y * 7 + x, bit i of a bitboard = square i.
Input:   float32 [N, 4, 7, 7]  channels = friendly (side to move), enemy, blocked, constant 1.0.
Action:  index = kind * 49 + to_square, 17 kinds = 833 actions.
         kind 0 = clone to `to_square` (source irrelevant), kind 1..16 = jump whose source is
         to_square + (dx, dy) for the 16 Chebyshev-distance-2 offsets in row-major order.
Outputs: policy_logits float32 [N, 833] (unmasked), value float32 [N, 1] in [-1, 1], side-to-move perspective.
"""

import struct
import zlib

import numpy as np
import torch
import torch.nn.functional as F

BOARD_SIZE = 7
SQUARES = BOARD_SIZE * BOARD_SIZE
CHANNELS = 4
KINDS = 17
ACTION_COUNT = KINDS * SQUARES
SYMMETRIES = 8
BOARD_MASK = (1 << SQUARES) - 1

CONTRACT_ID = "attax-pv-1"
INPUT_NAME = "board"
OUTPUT_POLICY = "policy_logits"
OUTPUT_VALUE = "value"
ONNX_OPSET = 17

# Source minus destination for jump kinds 1..16 (row-major dy, dx in -2..2 at Chebyshev distance 2).
JUMP_OFFSETS = [(dx, dy) for dy in range(-2, 3) for dx in range(-2, 3) if max(abs(dx), abs(dy)) == 2]
assert len(JUMP_OFFSETS) == 16
_KIND_FROM_OFFSET = {off: k for k, off in enumerate(JUMP_OFFSETS, start=1)}

NO_ACTION = -1
TERMINATION_TERMINAL = 0
TERMINATION_PLY_CAP = 1


def transform_xy(x, y, sym):
    """Same formulas as BitboardOps.TransformXY (affine, valid for off-board coordinates too)."""
    n = BOARD_SIZE
    if sym == 0:
        return x, y
    if sym == 1:
        return n - 1 - y, x
    if sym == 2:
        return n - 1 - x, n - 1 - y
    if sym == 3:
        return y, n - 1 - x
    if sym == 4:
        return x, n - 1 - y
    if sym == 5:
        return n - 1 - x, y
    if sym == 6:
        return y, x
    if sym == 7:
        return n - 1 - y, n - 1 - x
    raise ValueError(f"symmetry must be in 0..7, got {sym}")


def _build_tables():
    sq_perm = np.zeros((SYMMETRIES, SQUARES), dtype=np.int64)  # new index of old square
    sq_inv = np.zeros((SYMMETRIES, SQUARES), dtype=np.int64)   # old index of new square
    act_perm = np.zeros((SYMMETRIES, ACTION_COUNT), dtype=np.int64)
    for s in range(SYMMETRIES):
        for sq in range(SQUARES):
            tx, ty = transform_xy(sq % BOARD_SIZE, sq // BOARD_SIZE, s)
            sq_perm[s, sq] = ty * BOARD_SIZE + tx
            sq_inv[s, ty * BOARD_SIZE + tx] = sq
        for a in range(ACTION_COUNT):
            kind, to = divmod(a, SQUARES)
            tx, ty = to % BOARD_SIZE, to // BOARD_SIZE
            ntx, nty = transform_xy(tx, ty, s)
            nto = nty * BOARD_SIZE + ntx
            if kind == 0:
                act_perm[s, a] = nto
                continue
            dx, dy = JUMP_OFFSETS[kind - 1]
            nfx, nfy = transform_xy(tx + dx, ty + dy, s)
            act_perm[s, a] = _KIND_FROM_OFFSET[(nfx - ntx, nfy - nty)] * SQUARES + nto
    return sq_perm, sq_inv, act_perm


SQ_PERM, SQ_INV, ACT_PERM = _build_tables()


def jump_source(action):
    """Source square of a jump action, or -1 if it lies off the board. Raises for clone actions."""
    kind, to = divmod(action, SQUARES)
    if kind == 0:
        raise ValueError("clone actions have no fixed source")
    dx, dy = JUMP_OFFSETS[kind - 1]
    fx, fy = to % BOARD_SIZE + dx, to // BOARD_SIZE + dy
    if not (0 <= fx < BOARD_SIZE and 0 <= fy < BOARD_SIZE):
        return -1
    return fy * BOARD_SIZE + fx


# ---------------------------------------------------------------------------------------------------------
# Split identity
# ---------------------------------------------------------------------------------------------------------

def uid_hash(uid):
    """Stable 32-bit hash of a game uid. Identical to Attax.Data.LogV3.UidHash."""
    return zlib.crc32(struct.pack("<Q", int(uid))) & 0xFFFFFFFF


def in_validation_split(uid, val_fraction):
    if val_fraction <= 0.0:
        return False
    if val_fraction >= 1.0:
        return True
    return (uid_hash(uid) / 4294967296.0) < val_fraction


# ---------------------------------------------------------------------------------------------------------
# Tensor helpers (all vectorised, device agnostic)
# ---------------------------------------------------------------------------------------------------------

_table_cache = {}


def _tables(device):
    key = str(device)
    t = _table_cache.get(key)
    if t is None:
        t = {
            "bits": torch.arange(SQUARES, dtype=torch.int64, device=device),
            "sq_inv": torch.from_numpy(SQ_INV).to(device),
            "act_perm": torch.from_numpy(ACT_PERM).to(device),
        }
        _table_cache[key] = t
    return t


def unpack_bits(bits):
    """int64 bitboards [N] -> float32 [N, 49]."""
    tb = _tables(bits.device)
    return ((bits[:, None] >> tb["bits"]) & 1).to(torch.float32)


def make_planes(red, blue, blocked, side):
    """Bitboards (int64 [N]) and side (0 red / 1 blue, [N]) -> float32 [N, 4, 7, 7] in the model input layout."""
    red_f = unpack_bits(red)
    blue_f = unpack_bits(blue)
    blocked_f = unpack_bits(blocked)
    is_red = (side == 0)[:, None]
    friendly = torch.where(is_red, red_f, blue_f)
    enemy = torch.where(is_red, blue_f, red_f)
    const = torch.ones_like(friendly)
    x = torch.stack([friendly, enemy, blocked_f, const], dim=1)
    return x.reshape(-1, CHANNELS, BOARD_SIZE, BOARD_SIZE)


def legal_mask_from_planes(x):
    """float32 planes [N, 4, 7, 7] -> bool legal-action mask [N, 833] for the side to move (channel 0)."""
    n = x.shape[0]
    friendly = x[:, 0] > 0.5
    empty = (x[:, 0] + x[:, 1] + x[:, 2]) < 0.5
    fp = F.pad(friendly.to(torch.float32), (2, 2, 2, 2))  # [N, 11, 11], zero outside the board
    hi = 2 + BOARD_SIZE

    def shifted(dx, dy):
        return fp[:, 2 + dy:hi + dy, 2 + dx:hi + dx] > 0.5

    clone = torch.zeros_like(friendly)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            if dx or dy:
                clone = clone | shifted(dx, dy)
    kinds = [clone] + [shifted(dx, dy) for dx, dy in JUMP_OFFSETS]
    mask = torch.stack(kinds, dim=1) & empty[:, None]
    return mask.reshape(n, ACTION_COUNT)


def apply_symmetry_to_planes(x, sym):
    """x [N, 4, 7, 7], sym long [N] (0..7) -> planes of the transformed boards (same as BitboardOps.ApplySymmetry)."""
    n = x.shape[0]
    tb = _tables(x.device)
    idx = tb["sq_inv"][sym]                      # [N, 49]: new square -> old square
    xf = x.reshape(n, CHANNELS, SQUARES)
    out = torch.gather(xf, 2, idx[:, None, :].expand(n, CHANNELS, SQUARES))
    return out.reshape(n, CHANNELS, BOARD_SIZE, BOARD_SIZE)


def transform_actions(actions, sym):
    """actions long [N] (>= 0), sym long [N] -> transformed action indices."""
    tb = _tables(actions.device)
    return tb["act_perm"][sym, actions]


def reference_legal_actions(red, blue, blocked, side):
    """Slow first-principles legal action set for one position (python ints). Used only by tests."""
    friendly = red if side == 0 else blue
    occupied = red | blue | blocked
    legal = set()
    for to in range(SQUARES):
        if (occupied >> to) & 1:
            continue
        tx, ty = to % BOARD_SIZE, to // BOARD_SIZE
        for dy in range(-2, 3):
            for dx in range(-2, 3):
                if dx == 0 and dy == 0:
                    continue
                fx, fy = tx + dx, ty + dy
                if not (0 <= fx < BOARD_SIZE and 0 <= fy < BOARD_SIZE):
                    continue
                if not (friendly >> (fy * BOARD_SIZE + fx)) & 1:
                    continue
                if max(abs(dx), abs(dy)) == 1:
                    legal.add(to)
                else:
                    legal.add(_KIND_FROM_OFFSET[(dx, dy)] * SQUARES + to)
    return legal
