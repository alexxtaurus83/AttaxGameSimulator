"""Log v4 reader and dataset for the Ataxx policy/value trainer. Format spec: Attax.Data/LogV3.cs (the class keeps its v3 name).

Everything is loaded into memory (46 bytes per sample, so 10M samples ~ 460 MB) and validated strictly: a file with
an illegal action, a wrong CRC or an inconsistent teacher label or score raises instead of training on corrupt data.

Targets (kept deliberately separate):
  * policy target  = the TEACHER action, only where the TeacherValid flag is set. The played action is a rollout
    artefact (it may be random/exploratory) and is never a policy target.
  * value target, selected by TensorData(value_mode=...):
      "score"   (recommended) tanh(teacher_score / score_scale): the teacher search's value of the position for the side to move,
                only where TeacherScoreValid is set. Decisive terminal scores (about +/-1e9) saturate to +/-1.
      "outcome" the result of the PLAYED trajectory from the side to move's view, only for games that ended with a real terminal
                result (ply-cap games are excluded unless include_plycap=True). Noisy: measured sign accuracy 0.59.
      "mix"     value_mix * score target + (1 - value_mix) * outcome, only where both exist.
    Rows without the needed field are NOT trained on for the value head; nothing falls back silently to another target.
"""

import os
import struct
import zlib

import lz4.block
import numpy as np
import torch

import ataxx_common as C

MAGIC = b"ATLG"
VERSION = 4
SAMPLE_DTYPE = np.dtype(
    [
        ("gid", "<u8"),
        ("ply", "<u2"),
        ("red", "<u8"),
        ("blue", "<u8"),
        ("blocked", "<u8"),
        ("side", "u1"),
        ("played", "<i2"),
        ("teacher", "<i2"),
        ("flags", "u1"),
        ("sym", "u1"),
        ("teacher_id", "u1"),
        ("teacher_score", "<i4"),
    ]
)
assert SAMPLE_DTYPE.itemsize == 46

REC_GAME_HEADER, REC_SAMPLE_BLOCK, REC_GAME_RESULT, REC_TEACHER_CONFIG = 1, 2, 3, 4
FLAG_TEACHER_VALID = 1
FLAG_PLAYED_RANDOM = 2
FLAG_TEACHER_SCORE_VALID = 4
KNOWN_FLAGS = FLAG_TEACHER_VALID | FLAG_PLAYED_RANDOM | FLAG_TEACHER_SCORE_VALID


class LogFormatError(ValueError):
    pass


def _read_exact(f, n, what):
    data = f.read(n)
    if len(data) != n:
        raise LogFormatError(f"Unexpected end of file while reading {what}")
    return data


def iter_games(path):
    """Yield (header, samples_ndarray, result, teachers) per game. `teachers` is the dict declared so far."""
    teachers = {}
    with open(path, "rb") as f:
        head = f.read(6)
        if len(head) < 6 or head[:4] != MAGIC:
            raise LogFormatError(f"{path}: missing 'ATLG' header")
        version = struct.unpack("<H", head[4:])[0]
        if version != VERSION:
            raise LogFormatError(f"{path}: unsupported log version {version}; this trainer only reads version {VERSION}")

        header = None
        chunks = []
        while True:
            rec = f.read(1)
            if not rec:
                break
            rec = rec[0]
            if rec == REC_TEACHER_CONFIG:
                if header is not None:
                    raise LogFormatError(f"{path}: TeacherConfig inside a game")
                tid, kind, depth, nodes, scale, flags, model_hash = struct.unpack("<BBBifBQ", _read_exact(f, 20, "teacher config"))
                if tid == 0 or tid in teachers:
                    raise LogFormatError(f"{path}: bad or duplicate teacher id {tid}")
                teachers[tid] = {"id": tid, "kind": kind, "depth": depth, "node_budget": nodes, "root_bonus_scale": scale,
                                 "flags": flags, "model_hash": model_hash}
            elif rec == REC_GAME_HEADER:
                if header is not None:
                    raise LogFormatError(f"{path}: GameHeader before the previous game's GameResult")
                uid, seed, board, gen, red_mode, blue_mode = struct.unpack("<QQBHBB", _read_exact(f, 21, "game header"))
                if board != C.BOARD_SIZE:
                    raise LogFormatError(f"{path}: board size {board} unsupported")
                header = {"uid": uid, "seed": seed, "generation": gen, "red_mode": red_mode, "blue_mode": blue_mode}
                chunks = []
            elif rec == REC_SAMPLE_BLOCK:
                if header is None:
                    raise LogFormatError(f"{path}: SampleBlock outside a game")
                count, raw, comp_size, crc = struct.unpack("<iiiI", _read_exact(f, 16, "block header"))
                if count < 0 or raw != count * SAMPLE_DTYPE.itemsize or comp_size < 0:
                    raise LogFormatError(f"{path}: corrupt SampleBlock header")
                comp = _read_exact(f, comp_size, "block payload")
                if (zlib.crc32(comp) & 0xFFFFFFFF) != crc:
                    raise LogFormatError(f"{path}: SampleBlock CRC32 mismatch")
                data = lz4.block.decompress(comp, uncompressed_size=raw)
                if len(data) != raw:
                    raise LogFormatError(f"{path}: SampleBlock decompressed to the wrong size")
                chunks.append(np.frombuffer(data, dtype=SAMPLE_DTYPE, count=count))
            elif rec == REC_GAME_RESULT:
                if header is None:
                    raise LogFormatError(f"{path}: GameResult outside a game")
                uid, result, plies, term = struct.unpack("<QbHB", _read_exact(f, 12, "game result"))
                if uid != header["uid"]:
                    raise LogFormatError(f"{path}: result uid {uid} differs from header uid {header['uid']}")
                if result not in (-1, 0, 1):
                    raise LogFormatError(f"{path}: game {uid} result {result} out of range")
                if term not in (C.TERMINATION_TERMINAL, C.TERMINATION_PLY_CAP):
                    raise LogFormatError(f"{path}: game {uid} unknown termination {term}")
                samples = np.concatenate(chunks) if chunks else np.zeros(0, dtype=SAMPLE_DTYPE)
                yield header, samples, {"result_red": result, "plies": plies, "termination": term}, teachers
                header = None
                chunks = []
            else:
                raise LogFormatError(f"{path}: unknown record type {rec}")
        if header is not None:
            raise LogFormatError(f"{path}: file ends inside a game (missing GameResult)")


def _check_samples(uid, s, teachers, path):
    if s.size == 0:
        return
    if np.any(s["gid"] != uid):
        raise LogFormatError(f"{path}: game {uid} contains samples with a different uid")
    if np.any(s["side"] > 1):
        raise LogFormatError(f"{path}: game {uid} has an invalid side value")
    if np.any(s["sym"] > 7):
        raise LogFormatError(f"{path}: game {uid} has an invalid symmetry value")
    if np.any(s["flags"] & ~np.uint8(KNOWN_FLAGS)):
        raise LogFormatError(f"{path}: game {uid} has unknown flags")
    red, blue, blocked = s["red"], s["blue"], s["blocked"]
    if np.any(((red | blue | blocked) >> np.uint64(C.SQUARES)) != 0):
        raise LogFormatError(f"{path}: game {uid} has bits outside the board")
    if np.any((red & blue) | (red & blocked) | (blue & blocked)):
        raise LogFormatError(f"{path}: game {uid} has overlapping bitboards")
    valid = (s["flags"] & FLAG_TEACHER_VALID) != 0
    if np.any(valid):
        if np.any(s["teacher_id"][valid] == 0) or not set(np.unique(s["teacher_id"][valid]).tolist()) <= set(teachers):
            raise LogFormatError(f"{path}: game {uid} has a valid teacher label without a declared teacher config")
        if np.any((s["teacher"][valid] < 0) | (s["teacher"][valid] >= C.ACTION_COUNT)):
            raise LogFormatError(f"{path}: game {uid} has a teacher action out of range")
    if np.any(s["teacher"][~valid] != C.NO_ACTION):
        raise LogFormatError(f"{path}: game {uid} has a teacher action but TeacherValid is clear")
    if np.any(s["teacher_id"][~valid] != 0):
        raise LogFormatError(f"{path}: game {uid} has a teacher id but TeacherValid is clear")
    score_valid = (s["flags"] & FLAG_TEACHER_SCORE_VALID) != 0
    if np.any(score_valid & ~valid):
        raise LogFormatError(f"{path}: game {uid} has a teacher score flag without a valid teacher label")
    if np.any(s["teacher_score"][~score_valid] != 0):
        raise LogFormatError(f"{path}: game {uid} has a teacher score but TeacherScoreValid is clear")
    if np.any((s["played"] < 0) | (s["played"] >= C.ACTION_COUNT)):
        raise LogFormatError(f"{path}: game {uid} has a played action out of range")


def _check_legality(arrays, chunk=65536):
    """Every played action (and every valid teacher action) must be legal in its position."""
    n = arrays["red"].shape[0]
    for start in range(0, n, chunk):
        sl = slice(start, min(n, start + chunk))
        x = C.make_planes(
            torch.from_numpy(arrays["red"][sl].astype(np.int64)),
            torch.from_numpy(arrays["blue"][sl].astype(np.int64)),
            torch.from_numpy(arrays["blocked"][sl].astype(np.int64)),
            torch.from_numpy(arrays["side"][sl].astype(np.int64)),
        )
        legal = C.legal_mask_from_planes(x)
        played = torch.from_numpy(arrays["played"][sl].astype(np.int64))
        ok = legal.gather(1, played[:, None])[:, 0]
        if not bool(ok.all()):
            i = int((~ok).nonzero()[0])
            raise LogFormatError(f"Illegal played action {int(played[i])} in game {int(arrays['uid'][start + i])} (sample {start + i})")
        tv = torch.from_numpy(arrays["teacher_valid"][sl])
        if bool(tv.any()):
            teacher = torch.from_numpy(arrays["teacher"][sl].astype(np.int64)).clamp(min=0)
            tok = legal.gather(1, teacher[:, None])[:, 0] | ~tv
            if not bool(tok.all()):
                i = int((~tok).nonzero()[0])
                raise LogFormatError(f"Illegal teacher action {int(teacher[i])} in game {int(arrays['uid'][start + i])}")


class Dataset:
    """Flat per-sample arrays (numpy) plus per-game bookkeeping."""

    def __init__(self, arrays, games, teachers, stats):
        self.a = arrays
        self.games = games          # {uid: {"seed","generation","result_red","termination","split_val": bool}}
        self.teachers = teachers
        self.stats = stats

    def __len__(self):
        return int(self.a["red"].shape[0])

    def subset(self, mask):
        return Dataset({k: v[mask] for k, v in self.a.items()}, self.games, self.teachers, self.stats)


def load_logs(paths, val_fraction=0.05, include_plycap=False, validate=True, max_samples=0):
    """Load and validate one or more v3 logs. Returns (train Dataset, val Dataset)."""
    files = []
    for p in paths:
        if os.path.isdir(p):
            files += sorted(os.path.join(p, n) for n in os.listdir(p) if n.endswith(".bin"))
        else:
            files.append(p)
    if not files:
        raise FileNotFoundError("No .bin log files found")

    cols = {k: [] for k in ("uid", "ply", "red", "blue", "blocked", "side", "played", "teacher", "teacher_valid", "played_random", "sym", "teacher_id", "value", "value_valid", "score", "score_valid")}
    games = {}
    game_file = {}
    teachers = {}
    n_total = 0
    n_plycap_games = 0

    for path in files:
        for header, s, result, file_teachers in iter_games(path):
            uid = header["uid"]
            # The same game appearing in two files means duplicated training data (a rerun with the same --seed and
            # --generation, or an original log loaded together with its relabeled copy). Symmetry copies are fine: they
            # live inside one file. Pass exactly one version of each game.
            if uid in game_file and game_file[uid] != path:
                raise LogFormatError(
                    f"Game uid {uid} appears in both '{game_file[uid]}' and '{path}'. These files contain the same game "
                    f"(same --seed and --generation, or a log plus its relabeled copy); loading both would count it twice. "
                    f"Use a different --seed per run, or pass only the relabeled file.")
            game_file[uid] = path
            for tid, t in file_teachers.items():
                if tid in teachers and teachers[tid] != t:
                    raise LogFormatError(f"{path}: teacher id {tid} is defined differently in another file; teacher ids must be globally consistent")
                teachers[tid] = t
            ident = (header["seed"], header["generation"])
            if uid in games and (games[uid]["seed"], games[uid]["generation"]) != ident:
                raise LogFormatError(f"{path}: game uid {uid} collides with a different game (seed/generation differ)")
            _check_samples(uid, s, file_teachers, path)
            is_val = C.in_validation_split(uid, val_fraction)
            games[uid] = {**header, **result, "split_val": is_val}
            if result["termination"] == C.TERMINATION_PLY_CAP:
                n_plycap_games += 1
            if s.size == 0:
                continue
            side = s["side"].astype(np.int64)
            # Outcome of the PLAYED trajectory from the side to move's perspective.
            value = np.where(side == 0, result["result_red"], -result["result_red"]).astype(np.float32)
            vvalid = np.full(s.shape[0], result["termination"] == C.TERMINATION_TERMINAL or include_plycap, dtype=np.bool_)
            cols["uid"].append(s["gid"].astype(np.uint64))
            cols["ply"].append(s["ply"].astype(np.int32))
            cols["red"].append(s["red"].astype(np.uint64))
            cols["blue"].append(s["blue"].astype(np.uint64))
            cols["blocked"].append(s["blocked"].astype(np.uint64))
            cols["side"].append(s["side"].astype(np.uint8))
            cols["played"].append(s["played"].astype(np.int16))
            cols["teacher"].append(s["teacher"].astype(np.int16))
            cols["teacher_valid"].append((s["flags"] & FLAG_TEACHER_VALID) != 0)
            cols["played_random"].append((s["flags"] & FLAG_PLAYED_RANDOM) != 0)
            cols["sym"].append(s["sym"].astype(np.uint8))
            cols["teacher_id"].append(s["teacher_id"].astype(np.uint8))
            cols["value"].append(value)
            cols["value_valid"].append(vvalid)
            cols["score"].append(s["teacher_score"].astype(np.float32))
            cols["score_valid"].append((s["flags"] & FLAG_TEACHER_SCORE_VALID) != 0)
            n_total += s.shape[0]
            if max_samples and n_total >= max_samples:
                break
        if max_samples and n_total >= max_samples:
            break

    if n_total == 0:
        raise LogFormatError("The logs contain no samples")

    arrays = {k: np.concatenate(v) for k, v in cols.items()}
    if validate:
        _check_legality(arrays)

    uids = arrays["uid"]
    val_mask = np.array([games[int(u)]["split_val"] for u in np.unique(uids)])
    uniq = np.unique(uids)
    val_uids = set(int(u) for u in uniq[val_mask])
    is_val = np.fromiter((int(u) in val_uids for u in uids), dtype=np.bool_, count=uids.shape[0])

    stats = {
        "files": len(files),
        "games": len(games),
        "samples": int(n_total),
        "plycap_games": n_plycap_games,
        "teacher_valid": int(arrays["teacher_valid"].sum()),
        "value_valid": int(arrays["value_valid"].sum()),
        "score_valid": int(arrays["score_valid"].sum()),
        "played_random": int(arrays["played_random"].sum()),
        "val_games": len(val_uids),
        "val_samples": int(is_val.sum()),
    }
    full = Dataset(arrays, games, teachers, stats)
    return full.subset(~is_val), full.subset(is_val)


def _to_i64(a):
    """uint64 bitboards (< 2^49) -> int64 without copying through python ints."""
    return a.view(np.int64) if a.dtype == np.uint64 else a.astype(np.int64)


VALUE_MODES = ("score", "outcome", "mix")
DEFAULT_SCORE_SCALE = 4.0e6


def score_target(score, scale):
    """tanh(score / scale). Engine score units are heuristic points x 10,000: ordinary positions sit within about +/-1e7, decisive
    terminal results at about +/-1e9. With scale 4e6 the typical median position (about 1.5e6) maps to 0.36 and the 99th percentile
    (about 7e6) to 0.94, so the ordinary range is not squashed, and every decisive result saturates to exactly +/-1."""
    return torch.tanh(score / scale)


class TensorData:
    """A Dataset moved to a torch device; batches are assembled on device."""

    def __init__(self, ds, device, value_mode="outcome", score_scale=DEFAULT_SCORE_SCALE, value_mix=0.5):
        if value_mode not in VALUE_MODES:
            raise ValueError(f"value_mode must be one of {VALUE_MODES}, got {value_mode!r}")
        if not score_scale > 0:
            raise ValueError("score_scale must be > 0")
        if not 0.0 <= value_mix <= 1.0:
            raise ValueError("value_mix must be in [0, 1]")
        a = ds.a
        t = lambda arr, dt=None: torch.from_numpy(np.ascontiguousarray(arr if dt is None else arr.astype(dt))).to(device)
        self.n = len(ds)
        self.red = t(_to_i64(a["red"]))
        self.blue = t(_to_i64(a["blue"]))
        self.blocked = t(_to_i64(a["blocked"]))
        self.side = t(a["side"], np.int64)
        self.played = t(a["played"], np.int64)
        self.teacher = t(a["teacher"], np.int64)
        self.teacher_valid = t(a["teacher_valid"])
        outcome, outcome_ok = t(a["value"]), t(a["value_valid"])
        score, score_ok = t(a["score"]), t(a["score_valid"])
        self.value_mode, self.score_scale, self.value_mix = value_mode, float(score_scale), float(value_mix)
        if value_mode == "outcome":
            self.value, self.value_valid = outcome, outcome_ok
        elif value_mode == "score":
            self.value, self.value_valid = score_target(score, self.score_scale), score_ok
        else:
            self.value = value_mix * score_target(score, self.score_scale) + (1.0 - value_mix) * outcome
            self.value_valid = score_ok & outcome_ok
        self.device = device

    def drop_untargeted(self):
        """Remove rows with neither a policy label nor a value target. Returns (new TensorData, number dropped)."""
        keep = self.teacher_valid | self.value_valid
        dropped = int((~keep).sum())
        if dropped == 0:
            return self, 0
        out = object.__new__(TensorData)
        out.__dict__.update(self.__dict__)
        for name in ("red", "blue", "blocked", "side", "played", "teacher", "teacher_valid", "value", "value_valid"):
            setattr(out, name, getattr(self, name)[keep])
        out.n = int(keep.sum())
        return out, dropped

    def batch(self, idx, augment=False, generator=None):
        x = C.make_planes(self.red[idx], self.blue[idx], self.blocked[idx], self.side[idx])
        teacher = self.teacher[idx]
        played = self.played[idx]
        if augment:
            sym = torch.randint(0, C.SYMMETRIES, (idx.shape[0],), device=self.device, generator=generator)
            x = C.apply_symmetry_to_planes(x, sym)
            teacher = torch.where(self.teacher_valid[idx], C.transform_actions(teacher.clamp(min=0), sym), teacher)
            played = C.transform_actions(played, sym)
        return {
            "x": x,
            "teacher": teacher,
            "teacher_valid": self.teacher_valid[idx],
            "played": played,
            "value": self.value[idx],
            "value_valid": self.value_valid[idx],
        }
