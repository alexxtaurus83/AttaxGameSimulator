import argparse
import glob
import json
import os
import random
import struct
import time
import zlib
from dataclasses import dataclass

import lz4.block
import numpy as np
import torch
import torch.nn as nn
import torch.optim as optim
from torch.utils.data import DataLoader, IterableDataset, TensorDataset, get_worker_info

# --- Configuration ---
BATCH_SIZE = 1024
LEARNING_RATE = 0.001
EPOCHS = 10
BOARD_SIZE = 7
INPUT_CHANNELS = 4  # Friendly, Enemy, Blocked, Constant
MODEL_PATH = "ataxx_value.onnx"
DATA_DIR = "."  # Directory containing .bin files

SAMPLE_SIZE = 32
SAMPLE_DTYPE = np.dtype(
    [
        ("gid", "<u4"),
        ("ply", "<u2"),
        ("red", "<u8"),
        ("blue", "<u8"),
        ("blocked", "<u8"),
        ("side", "u1"),
        ("rules", "u1"),
    ]
)

# Precompute bit positions/masks for vectorized conversion.
BIT_POSITIONS = np.arange(BOARD_SIZE * BOARD_SIZE, dtype=np.uint64)
BIT_MASKS = (np.uint64(1) << BIT_POSITIONS)[None, :]


def bitboards_to_numpy_batch(red, blue, blocked, side):
    """Convert bitboards to model input array with shape [N, 4, 7, 7]."""
    side = side.astype(np.uint8, copy=False)
    friendly = np.where(side == 0, red, blue).astype(np.uint64, copy=False)
    enemy = np.where(side == 0, blue, red).astype(np.uint64, copy=False)
    blocked = blocked.astype(np.uint64, copy=False)

    f_plane = ((friendly[:, None] & BIT_MASKS) != 0).astype(np.float32).reshape(-1, BOARD_SIZE, BOARD_SIZE)
    e_plane = ((enemy[:, None] & BIT_MASKS) != 0).astype(np.float32).reshape(-1, BOARD_SIZE, BOARD_SIZE)
    b_plane = ((blocked[:, None] & BIT_MASKS) != 0).astype(np.float32).reshape(-1, BOARD_SIZE, BOARD_SIZE)
    c_plane = np.ones_like(f_plane, dtype=np.float32)

    return np.stack([f_plane, e_plane, b_plane, c_plane], axis=1)


def game_in_validation_split(game_id, val_fraction):
    if val_fraction <= 0.0:
        return False
    if val_fraction >= 1.0:
        return True

    # Deterministic game-id based split.
    gid_bytes = struct.pack("<I", game_id)
    h = zlib.crc32(gid_bytes) & 0xFFFFFFFF
    return (h / 4294967296.0) < val_fraction


@dataclass
class TrainerConfig:
    board_size: int = BOARD_SIZE
    expected_orth_capture: int = 0


class AtaxxStreamingDataset(IterableDataset):
    """Stream batches from binary training logs with vectorized parsing and expansion."""

    def __init__(
        self,
        file_paths,
        split="train",
        val_fraction=0.05,
        batch_size=BATCH_SIZE,
        strict_metadata=True,
        skip_bad_games=False,
        trainer_config=None,
        shuffle_buffer=0,
    ):
        super().__init__()
        self.file_paths = list(file_paths)
        self.split = split
        self.val_fraction = float(val_fraction)
        self.batch_size = max(1, int(batch_size))
        self.strict_metadata = bool(strict_metadata)
        self.skip_bad_games = bool(skip_bad_games)
        self.trainer_config = trainer_config if trainer_config is not None else TrainerConfig()
        self.shuffle_buffer = max(0, int(shuffle_buffer))

    def _iter_file_samples(self, path):
        # game_id -> list[np.ndarray[SAMPLE_DTYPE]]
        file_samples = {}
        # game_id -> (rule_flags, board_size)
        game_headers = {}

        board_bits = self.trainer_config.board_size * self.trainer_config.board_size
        board_mask = np.uint64((1 << board_bits) - 1)
        expected_rule_flag = np.uint8(1 if self.trainer_config.expected_orth_capture else 0)

        with open(path, "rb") as f:
            # v1 header: [magic="ATLG"][version=ushort] = 6 bytes. Required.
            maybe_magic = f.read(4)
            if len(maybe_magic) < 4 or maybe_magic != b"ATLG":
                raise ValueError(
                    f"File {path} is missing the 'ATLG' header. "
                    "Legacy v0 format is no longer supported."
                )

            version_bytes = f.read(2)
            if len(version_bytes) != 2:
                raise ValueError("Invalid file header: missing format version")

            version = struct.unpack("<H", version_bytes)[0]
            if version != 1:
                raise ValueError(f"Unsupported training log format version: {version}")

            while True:
                type_byte = f.read(1)
                if not type_byte:
                    break

                record_type = type_byte[0]

                if record_type == 1:  # GameHeader
                    # [GameId:4][Seed:8][RuleFlags:1][BoardSize:1] = 14 bytes
                    header = f.read(14)
                    if len(header) != 14:
                        raise ValueError("Invalid game header record: expected 14 bytes")

                    game_id, _seed, rule_flags, board_size = struct.unpack("<IQBB", header)
                    game_headers[game_id] = (rule_flags, board_size)

                    if board_size != self.trainer_config.board_size:
                        msg = (
                            f"Board size mismatch for game {game_id}: "
                            f"header={board_size}, expected={self.trainer_config.board_size}"
                        )
                        if self.strict_metadata:
                            raise ValueError(msg)
                        if self.skip_bad_games:
                            file_samples.pop(game_id, None)
                            game_headers.pop(game_id, None)
                    continue

                if record_type == 2:  # CompressedSampleBlock
                    header_data = f.read(16)
                    if len(header_data) != 16:
                        raise ValueError("Invalid compressed block header: expected 16 bytes")

                    count, uncomp_size, comp_size, crc32 = struct.unpack("<IIII", header_data)

                    compressed_data = f.read(comp_size)
                    if len(compressed_data) != comp_size:
                        raise ValueError("Invalid compressed block: unexpected end of file")

                    actual_crc32 = zlib.crc32(compressed_data) & 0xFFFFFFFF
                    expected_crc32 = crc32 & 0xFFFFFFFF
                    if actual_crc32 != expected_crc32:
                        raise ValueError(
                            f"CRC32 mismatch in compressed block: expected {expected_crc32:#010x}, got {actual_crc32:#010x}"
                        )

                    raw_bytes = lz4.block.decompress(compressed_data, uncompressed_size=uncomp_size)
                    expected_raw_size = count * SAMPLE_SIZE
                    if len(raw_bytes) < expected_raw_size:
                        raise ValueError(
                            f"Invalid decompressed block size: expected at least {expected_raw_size} bytes, got {len(raw_bytes)}"
                        )

                    samples = np.frombuffer(raw_bytes, dtype=SAMPLE_DTYPE, count=count)
                    if samples.size == 0:
                        continue

                    valid_mask = np.ones(samples.shape[0], dtype=np.bool_)

                    side_ok = (samples["side"] == 0) | (samples["side"] == 1)
                    if not np.all(side_ok):
                        bad_idx = np.flatnonzero(~side_ok)[0]
                        bad_gid = int(samples["gid"][bad_idx])
                        bad_side = int(samples["side"][bad_idx])
                        msg = f"Invalid side value {bad_side} in game {bad_gid}"
                        if self.strict_metadata:
                            raise ValueError(msg)
                        if self.skip_bad_games:
                            valid_mask &= side_ok

                    red = samples["red"].astype(np.uint64, copy=False)
                    blue = samples["blue"].astype(np.uint64, copy=False)
                    blocked = samples["blocked"].astype(np.uint64, copy=False)

                    overlap_ok = ((red & blue) == 0) & ((red & blocked) == 0) & ((blue & blocked) == 0)
                    if not np.all(overlap_ok):
                        bad_idx = np.flatnonzero(~overlap_ok)[0]
                        bad_gid = int(samples["gid"][bad_idx])
                        msg = f"Overlapping bitboards in game {bad_gid}"
                        if self.strict_metadata:
                            raise ValueError(msg)
                        if self.skip_bad_games:
                            valid_mask &= overlap_ok

                    in_range_ok = (((red | blue | blocked) & ~board_mask) == 0)
                    if not np.all(in_range_ok):
                        bad_idx = np.flatnonzero(~in_range_ok)[0]
                        bad_gid = int(samples["gid"][bad_idx])
                        msg = f"Out-of-range bits found in game {bad_gid}"
                        if self.strict_metadata:
                            raise ValueError(msg)
                        if self.skip_bad_games:
                            valid_mask &= in_range_ok

                    gids = samples["gid"]
                    if gids.size > 0:
                        for gid_u in np.unique(gids):
                            gid = int(gid_u)
                            if gid not in game_headers:
                                continue

                            header_rule_flags, header_board_size = game_headers[gid]
                            gid_mask = gids == gid_u

                            if header_board_size != self.trainer_config.board_size:
                                msg = (
                                    f"Game {gid} header board size mismatch: "
                                    f"{header_board_size} vs expected {self.trainer_config.board_size}"
                                )
                                if self.strict_metadata:
                                    raise ValueError(msg)
                                if self.skip_bad_games:
                                    valid_mask &= ~gid_mask
                                continue

                            sample_rule_bits = samples["rules"][gid_mask] & np.uint8(0b00000001)
                            header_rule_bit = np.uint8(header_rule_flags & 0b00000001)

                            rule_match_ok = np.all(sample_rule_bits == header_rule_bit)
                            if not rule_match_ok:
                                msg = (
                                    f"Rule mismatch in game {gid}: "
                                    f"sample rules={int(sample_rule_bits[0])}, header rules={header_rule_flags}"
                                )
                                if self.strict_metadata:
                                    raise ValueError(msg)
                                if self.skip_bad_games:
                                    valid_mask &= ~gid_mask
                                continue

                            expected_rule_ok = np.all(sample_rule_bits == expected_rule_flag)
                            if not expected_rule_ok:
                                msg = (
                                    f"Unexpected capture rule in game {gid}: sample={int(sample_rule_bits[0])}, "
                                    f"expected={int(expected_rule_flag)}"
                                )
                                if self.strict_metadata:
                                    raise ValueError(msg)
                                if self.skip_bad_games:
                                    valid_mask &= ~gid_mask

                    if self.skip_bad_games:
                        samples = samples[valid_mask]

                    if samples.size == 0:
                        continue

                    for gid_u in np.unique(samples["gid"]):
                        gid = int(gid_u)
                        gid_samples = samples[samples["gid"] == gid_u]
                        if gid_samples.size == 0:
                            continue
                        if gid not in file_samples:
                            file_samples[gid] = []
                        file_samples[gid].append(gid_samples)

                        if len(file_samples) > 100:
                            print(
                                f"WARNING: {len(file_samples)} outstanding games in {path}. "
                                "This suggests GameResult records are delayed or missing."
                            )
                    continue

                if record_type == 3:  # GameResult
                    data = f.read(7)
                    if len(data) != 7:
                        raise ValueError("Invalid game result record: expected 7 bytes")

                    game_id, result_byte, total_plies = struct.unpack("<IbH", data)
                    _ = total_plies

                    if game_id not in file_samples:
                        # Game had a header but no samples — valid for --samples 0 runs.
                        game_headers.pop(game_id, None)
                        continue

                    is_val = game_in_validation_split(game_id, self.val_fraction)
                    use_for_this_dataset = (self.split == "val" and is_val) or (
                        self.split == "train" and not is_val
                    )

                    if use_for_this_dataset:
                        game_chunks = file_samples[game_id]
                        game_samples = game_chunks[0] if len(game_chunks) == 1 else np.concatenate(game_chunks, axis=0)

                        x = bitboards_to_numpy_batch(
                            game_samples["red"],
                            game_samples["blue"],
                            game_samples["blocked"],
                            game_samples["side"],
                        )
                        final_result = np.float32(result_byte)
                        y = np.where(game_samples["side"] == 0, final_result, -final_result).astype(np.float32).reshape(-1, 1)
                        yield x, y

                    del file_samples[game_id]
                    game_headers.pop(game_id, None)
                    continue

                raise ValueError(f"Unknown record type byte: {record_type}")

            if file_samples:
                print(
                    f"WARNING: {len(file_samples)} game(s) had samples but no GameResult "
                    f"in {path} (game IDs: {sorted(file_samples.keys())[:20]}). "
                    f"These samples were discarded."
                )

    def __iter__(self):
        worker_info = get_worker_info()
        if worker_info is None:
            files = self.file_paths
            worker_id = 0
        else:
            files = self.file_paths[worker_info.id :: worker_info.num_workers]
            worker_id = int(worker_info.id)

        def iter_batched_stream():
            x_buffer = []
            y_buffer = []
            buffered = 0

            for path in files:
                if not os.path.exists(path):
                    continue

                for x_chunk, y_chunk in self._iter_file_samples(path):
                    if x_chunk.size == 0:
                        continue

                    x_buffer.append(x_chunk)
                    y_buffer.append(y_chunk)
                    buffered += int(x_chunk.shape[0])

                    while buffered >= self.batch_size:
                        x_all = x_buffer[0] if len(x_buffer) == 1 else np.concatenate(x_buffer, axis=0)
                        y_all = y_buffer[0] if len(y_buffer) == 1 else np.concatenate(y_buffer, axis=0)

                        x_batch = x_all[: self.batch_size]
                        y_batch = y_all[: self.batch_size]
                        yield torch.from_numpy(x_batch), torch.from_numpy(y_batch)

                        x_rem = x_all[self.batch_size :]
                        y_rem = y_all[self.batch_size :]
                        if x_rem.shape[0] > 0:
                            x_buffer = [x_rem]
                            y_buffer = [y_rem]
                            buffered = int(x_rem.shape[0])
                        else:
                            x_buffer = []
                            y_buffer = []
                            buffered = 0

            if buffered > 0:
                x_all = x_buffer[0] if len(x_buffer) == 1 else np.concatenate(x_buffer, axis=0)
                y_all = y_buffer[0] if len(y_buffer) == 1 else np.concatenate(y_buffer, axis=0)
                yield torch.from_numpy(x_all), torch.from_numpy(y_all)

        if self.shuffle_buffer <= 0:
            yield from iter_batched_stream()
            return

        seed = (time.time_ns() + 0x9E3779B97F4A7C15 * (worker_id + 1)) & ((1 << 64) - 1)
        rng = random.Random(seed)

        shuffle_batches = []
        shuffle_batch_sizes = []
        shuffle_samples = 0

        for batch in iter_batched_stream():
            batch_size = int(batch[0].shape[0])
            if shuffle_samples < self.shuffle_buffer:
                shuffle_batches.append(batch)
                shuffle_batch_sizes.append(batch_size)
                shuffle_samples += batch_size
                continue

            i = rng.randrange(len(shuffle_batches))
            yield shuffle_batches[i]
            shuffle_samples -= shuffle_batch_sizes[i]
            shuffle_batches[i] = batch
            shuffle_batch_sizes[i] = batch_size
            shuffle_samples += batch_size

        while shuffle_batches:
            i = rng.randrange(len(shuffle_batches))
            yield shuffle_batches.pop(i)
            shuffle_samples -= shuffle_batch_sizes.pop(i)


def materialize_first_n(dataset, n):
    """Collect first N samples from an iterable/batched dataset into a finite TensorDataset."""
    xs = []
    ys = []
    collected = 0

    for x_batch, y_batch in dataset:
        if isinstance(x_batch, torch.Tensor):
            x_np = x_batch.detach().cpu().numpy()
        else:
            x_np = np.asarray(x_batch)

        if isinstance(y_batch, torch.Tensor):
            y_np = y_batch.detach().cpu().numpy()
        else:
            y_np = np.asarray(y_batch)

        take = min(int(n) - collected, int(x_np.shape[0]))
        if take <= 0:
            break

        xs.append(np.ascontiguousarray(x_np[:take], dtype=np.float32))
        ys.append(np.ascontiguousarray(y_np[:take], dtype=np.float32))
        collected += take

        if collected >= n:
            break

    if not xs:
        return None

    x_all = np.concatenate(xs, axis=0)
    y_all = np.concatenate(ys, axis=0)
    return TensorDataset(torch.from_numpy(x_all), torch.from_numpy(y_all))


# --- Model Definition ---


class AtaxxValueNet(nn.Module):
    """Sentis-friendly model: No BatchNorm, uses GlobalAveragePool to reduce parameters."""

    def __init__(self):
        super(AtaxxValueNet, self).__init__()

        self.conv1 = nn.Conv2d(INPUT_CHANNELS, 64, kernel_size=3, padding=1)
        self.relu = nn.ReLU()

        self.conv2 = nn.Conv2d(64, 128, kernel_size=3, padding=1)
        self.conv3 = nn.Conv2d(128, 128, kernel_size=3, padding=1)

        # Global Average Pool: [Batch, 128, 7, 7] -> [Batch, 128, 1, 1]
        self.avgpool = nn.AdaptiveAvgPool2d((1, 1))

        self.fc1 = nn.Linear(128, 64)
        self.fc2 = nn.Linear(64, 1)
        self.tanh = nn.Tanh()

    def forward(self, x):
        x = self.relu(self.conv1(x))
        x = self.relu(self.conv2(x))
        x = self.relu(self.conv3(x))

        x = self.avgpool(x)
        x = torch.flatten(x, 1)

        x = self.relu(self.fc1(x))
        x = self.tanh(self.fc2(x))
        return x


# --- Training Loop ---


def load_trainer_config(config_path="attax.config.json"):
    cfg = TrainerConfig()

    if not os.path.exists(config_path):
        return cfg

    with open(config_path, "r", encoding="utf-8") as f:
        data = json.load(f)

    board = data.get("board", {})

    if "size" in board:
        cfg.board_size = int(board["size"])

    return cfg


def parse_args():
    parser = argparse.ArgumentParser(description="Train Ataxx value network")
    parser.add_argument(
        "--data",
        type=str,
        default=None,
        help="Path to a specific .bin file or directory (default: scan current directory)",
    )
    parser.add_argument(
        "--epochs",
        type=int,
        default=EPOCHS,
        help=f"Number of training epochs (default: {EPOCHS})",
    )
    parser.add_argument(
        "--batch-size",
        type=int,
        default=BATCH_SIZE,
        help=f"Batch size (default: {BATCH_SIZE})",
    )
    parser.add_argument(
        "--device",
        choices=["auto", "cpu", "cuda"],
        default="auto",
        help="Device selection (default: auto)",
    )
    parser.add_argument(
        "--check-onnx",
        action="store_true",
        help="Validate exported ONNX model with ONNX checker and ONNX Runtime",
    )
    parser.add_argument(
        "--num-workers",
        type=int,
        default=0,
        help="DataLoader worker count (default: 0)",
    )
    parser.add_argument(
        "--pin-memory",
        action="store_true",
        help="Enable DataLoader pinned memory",
    )
    parser.add_argument(
        "--prefetch-factor",
        type=int,
        default=4,
        help="DataLoader prefetch factor (default: 4, used when num_workers > 0)",
    )
    parser.add_argument(
        "--shuffle-buffer",
        type=int,
        default=0,
        help="Approximate samples to hold in stream shuffle buffer (0 disables)",
    )

    amp_group = parser.add_mutually_exclusive_group()
    amp_group.add_argument(
        "--amp",
        dest="amp",
        action="store_true",
        help="Enable AMP (default: enabled on CUDA, disabled otherwise)",
    )
    amp_group.add_argument(
        "--no-amp",
        dest="amp",
        action="store_false",
        help="Disable AMP",
    )
    parser.set_defaults(amp=None)

    parser.add_argument(
        "--val-fraction",
        type=float,
        default=0.05,
        help="Validation fraction in [0,1], split deterministically by game id hash",
    )
    parser.add_argument(
        "--overfit-n",
        type=int,
        default=0,
        help="Overfit sanity mode: train on first N samples only (0 disables)",
    )

    parser.add_argument(
        "--strict-metadata",
        type=int,
        choices=[0, 1],
        default=1,
        help="Strict metadata checks while reading logs (default: 1)",
    )
    parser.add_argument(
        "--skip-bad-games",
        type=int,
        choices=[0, 1],
        default=0,
        help="Skip bad samples/games on metadata mismatch instead of failing (default: 0)",
    )
    parser.add_argument(
        "--expected-orth-capture",
        type=int,
        choices=[0, 1],
        default=None,
        help="Expected orthogonal-capture rule flag (0=standard, 1=orthogonal-only). Defaults to 0 if omitted.",
    )

    return parser.parse_args()


def resolve_device(device_mode):
    # CUDA-enabled PyTorch builds still support CPU execution, so --device cpu remains valid.
    if device_mode == "cpu":
        return torch.device("cpu")

    if device_mode == "cuda":
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA requested but not available")
        return torch.device("cuda")

    return torch.device("cuda" if torch.cuda.is_available() else "cpu")


def scan_training_data(file_paths):
    """Quickly scan binary logs to report statistics and catch writer issues."""
    print("=== Training Data Scan ===")
    total_games = 0
    total_samples = 0
    games_with_results = set()

    for path in file_paths:
        if not os.path.exists(path):
            continue
        with open(path, "rb") as f:
            maybe_magic = f.read(4)
            if maybe_magic != b"ATLG":
                print(f"WARNING: Skipping {path} — missing 'ATLG' header (unsupported legacy format).")
                continue
            f.read(2)  # version (already validated by _iter_file_samples if training)

            while True:
                type_byte = f.read(1)
                if not type_byte:
                    break
                record_type = type_byte[0]

                if record_type == 1:  # GameHeader
                    f.read(14)
                elif record_type == 2:  # CompressedSampleBlock
                    header_data = f.read(16)
                    if len(header_data) != 16:
                        break
                    count, uncomp_size, comp_size, crc32 = struct.unpack("<IIII", header_data)
                    f.seek(comp_size, 1)  # Skip payload
                    total_samples += count
                elif record_type == 3:  # GameResult
                    data = f.read(7)
                    if len(data) == 7:
                        game_id, result, plies = struct.unpack("<IbH", data)
                        games_with_results.add(game_id)
                        total_games += 1
                else:
                    break

    print(f"Total games (with results): {total_games}")
    print(f"Total samples: {total_samples}")
    if total_games > 0:
        print(f"Avg samples/game: {total_samples / total_games:.1f}")
    print("==========================")


def print_startup_diagnostics(device):
    print("=== Startup Diagnostics ===")
    print(f"torch.__version__: {torch.__version__}")
    print(f"torch.version.cuda: {torch.version.cuda}")
    print(f"torch.cuda.is_available(): {torch.cuda.is_available()}")

    current_device_index = "n/a"
    device_name = "n/a"
    if torch.cuda.is_available():
        current_device_index = torch.cuda.current_device()
        device_name = torch.cuda.get_device_name(current_device_index)

    print(f"Resolved device: {device}")
    print(f"Current CUDA device index: {current_device_index}")
    print(f"Current CUDA device name: {device_name}")

    if device.type == "cuda":
        cudnn_version = torch.backends.cudnn.version() if torch.backends.cudnn.is_available() else None
        print(f"torch.backends.cudnn.enabled: {torch.backends.cudnn.enabled}")
        print(f"torch.backends.cudnn.version(): {cudnn_version}")

    print("===========================")


def validate_onnx_export(model, model_path, batch_size=16, atol=1e-4, rtol=1e-3):
    import onnx
    import onnxruntime as ort

    print("Validating exported ONNX model...")
    onnx_model = onnx.load(model_path)
    onnx.checker.check_model(onnx_model)
    print("ONNX checker: OK")

    model = model.to("cpu")
    model.eval()

    rng = np.random.default_rng(0)
    x_np = rng.standard_normal((batch_size, INPUT_CHANNELS, BOARD_SIZE, BOARD_SIZE), dtype=np.float32)

    with torch.no_grad():
        x_torch = torch.from_numpy(x_np).to("cpu", non_blocking=False)
        torch_out = model(x_torch).cpu().numpy()

    session = ort.InferenceSession(model_path, providers=["CPUExecutionProvider"])
    input_name = session.get_inputs()[0].name
    output_name = session.get_outputs()[0].name
    ort_out = session.run([output_name], {input_name: x_np})[0]

    max_abs_diff = np.max(np.abs(torch_out - ort_out))
    print(f"ONNX Runtime max abs diff: {max_abs_diff:.6e}")

    if not np.allclose(torch_out, ort_out, atol=atol, rtol=rtol):
        raise AssertionError(
            f"ONNX Runtime output mismatch (atol={atol}, rtol={rtol}, max_abs_diff={max_abs_diff:.6e})"
        )

    print(f"ONNX Runtime consistency: OK (atol={atol}, rtol={rtol})")


def train(
    data_path=None,
    epochs=EPOCHS,
    batch_size=BATCH_SIZE,
    device_mode="auto",
    check_onnx=False,
    num_workers=0,
    pin_memory=False,
    prefetch_factor=4,
    amp=None,
    val_fraction=0.05,
    overfit_n=0,
    strict_metadata=True,
    skip_bad_games=False,
    expected_orth_capture=None,
    shuffle_buffer=0,
):
    device = resolve_device(device_mode)
    print_startup_diagnostics(device)
    print(f"Using device: {device}")

    # Find data files
    if data_path:
        if os.path.isdir(data_path):
            files = glob.glob(os.path.join(data_path, "*.bin"))
        else:
            files = [data_path]
    else:
        files = glob.glob(os.path.join(DATA_DIR, "*.bin"))

    if not files:
        print(f"No training data found. Run 'Attax.Console selfplayloggen' first.")
        return

    scan_training_data(files)

    val_fraction = max(0.0, min(1.0, float(val_fraction)))
    amp_enabled = (device.type == "cuda") if amp is None else bool(amp)
    amp_enabled = amp_enabled and device.type == "cuda"

    print(
        f"DataLoader settings: num_workers={num_workers}, pin_memory={pin_memory}, "
        f"prefetch_factor={prefetch_factor if num_workers > 0 else 'n/a'}, "
        f"shuffle_buffer={shuffle_buffer}"
    )
    if num_workers > 0:
        print(
            f"NOTE: {num_workers} DataLoader workers will each maintain independent "
            f"file buffers. Total RAM usage scales with worker count."
        )
    print(f"AMP enabled: {amp_enabled}")
    print(f"Validation fraction: {val_fraction:.3f}")

    trainer_config = load_trainer_config()
    if expected_orth_capture is not None:
        trainer_config.expected_orth_capture = int(expected_orth_capture)

    train_dataset = AtaxxStreamingDataset(
        files,
        split="train",
        val_fraction=val_fraction,
        batch_size=batch_size,
        strict_metadata=strict_metadata,
        skip_bad_games=skip_bad_games,
        trainer_config=trainer_config,
        shuffle_buffer=shuffle_buffer,
    )
    val_dataset = AtaxxStreamingDataset(
        files,
        split="val",
        val_fraction=val_fraction,
        batch_size=batch_size,
        strict_metadata=strict_metadata,
        skip_bad_games=skip_bad_games,
        trainer_config=trainer_config,
        shuffle_buffer=0,  # No shuffling needed for validation
    )

    if overfit_n > 0:
        print(f"Overfit mode active: materializing first {overfit_n} training samples")
        finite_train = materialize_first_n(train_dataset, overfit_n)
        if finite_train is None or len(finite_train) == 0:
            print("Dataset is empty.")
            return
        train_dataset = finite_train
        val_dataset = None

    train_loader_kwargs = {
        "batch_size": None,
        "num_workers": max(0, int(num_workers)),
        "pin_memory": bool(pin_memory),
    }
    if train_loader_kwargs["num_workers"] > 0:
        train_loader_kwargs["prefetch_factor"] = max(1, int(prefetch_factor))
        train_loader_kwargs["persistent_workers"] = True

    train_dataloader = DataLoader(train_dataset, **train_loader_kwargs)

    val_dataloader = None
    if val_dataset is not None and val_fraction > 0.0:
        val_loader_kwargs = {
            "batch_size": None,
            "num_workers": max(0, int(num_workers)),
            "pin_memory": bool(pin_memory),
        }
        if val_loader_kwargs["num_workers"] > 0:
            val_loader_kwargs["prefetch_factor"] = max(1, int(prefetch_factor))
            val_loader_kwargs["persistent_workers"] = True
        val_dataloader = DataLoader(val_dataset, **val_loader_kwargs)

    model = AtaxxValueNet().to(device)
    if device.type == "cuda":
        first_param = next(model.parameters(), None)
        if first_param is None:
            raise RuntimeError("CUDA was selected but model has no parameters to validate device placement")
        if not first_param.is_cuda:
            raise RuntimeError(
                "CUDA was selected but model parameters are not on CUDA "
                f"(first parameter device: {first_param.device})"
            )

    optimizer = optim.Adam(model.parameters(), lr=LEARNING_RATE, weight_decay=1e-4)
    criterion = nn.SmoothL1Loss()
    scaler = torch.amp.GradScaler("cuda", enabled=amp_enabled) if amp_enabled else None

    monitor_interval = 10
    log_interval_seconds = 2.0
    cuda_utilization_fn = getattr(torch.cuda, "utilization", None) if device.type == "cuda" else None
    if not callable(cuda_utilization_fn):
        cuda_utilization_fn = None

    last_train_loss = None

    first_train_batch_verified = False

    for epoch in range(int(epochs)):
        model.train()
        total_loss = 0.0
        count = 0
        start = time.time()

        running_data_ms = 0.0
        running_compute_ms = 0.0
        running_batches = 0

        if device.type == "cuda":
            torch.cuda.reset_peak_memory_stats(device)

        last_log_time = time.time()

        train_iter = iter(train_dataloader)
        while True:
            data_start = time.perf_counter()
            try:
                inputs, targets = next(train_iter)
            except StopIteration:
                break
            data_ms = (time.perf_counter() - data_start) * 1000.0

            compute_start = time.perf_counter()

            inputs = inputs.to(device, non_blocking=pin_memory)
            targets = targets.to(device, non_blocking=pin_memory)

            if device.type == "cuda" and not first_train_batch_verified:
                if not inputs.is_cuda:
                    raise RuntimeError(
                        "CUDA was selected but training input tensor remained on CPU "
                        f"(tensor device: {inputs.device})"
                    )
                if not targets.is_cuda:
                    raise RuntimeError(
                        "CUDA was selected but training target tensor remained on CPU "
                        f"(tensor device: {targets.device})"
                    )
                first_train_batch_verified = True

            optimizer.zero_grad(set_to_none=True)
            with torch.amp.autocast("cuda", enabled=amp_enabled):
                outputs = model(inputs)
                loss = criterion(outputs, targets)

            if scaler is not None:
                scaler.scale(loss).backward()
                scaler.step(optimizer)
                scaler.update()
            else:
                loss.backward()
                optimizer.step()

            needs_log = running_batches + 1 >= monitor_interval or (time.time() - last_log_time) >= log_interval_seconds
            if device.type == "cuda" and needs_log:
                torch.cuda.synchronize(device)
            compute_ms = (time.perf_counter() - compute_start) * 1000.0

            total_loss += loss.item()
            count += 1

            running_data_ms += data_ms
            running_compute_ms += compute_ms
            running_batches += 1

            if running_batches > 0 and needs_log:
                avg_data_ms = running_data_ms / running_batches
                avg_compute_ms = running_compute_ms / running_batches

                gpu_util = "n/a"
                if cuda_utilization_fn is not None:
                    try:
                        gpu_util = f"{float(cuda_utilization_fn()):.1f}%"
                    except Exception:
                        gpu_util = "n/a"

                mem_mb = 0.0
                peak_mem_mb = 0.0
                if device.type == "cuda":
                    mem_mb = torch.cuda.memory_allocated(device) / (1024.0 * 1024.0)
                    peak_mem_mb = torch.cuda.max_memory_allocated(device) / (1024.0 * 1024.0)

                print(
                    f"[Epoch {epoch + 1}, Batch {count}] Loss: {loss.item():.6f} | "
                    f"Data: {avg_data_ms:.1f} ms | Compute: {avg_compute_ms:.1f} ms | "
                    f"GPU Util: {gpu_util} | Mem: {mem_mb:.0f}/{peak_mem_mb:.0f} MB"
                )

                running_data_ms = 0.0
                running_compute_ms = 0.0
                running_batches = 0
                last_log_time = time.time()

        if count == 0:
            print("Training dataset produced no batches.")
            return

        train_loss = total_loss / count
        last_train_loss = train_loss

        msg = f"Epoch {epoch + 1}/{epochs}, Train Loss: {train_loss:.6f}, Time: {time.time() - start:.2f}s"

        if device.type == "cuda":
            mem_mb = torch.cuda.memory_allocated(device) / (1024.0 * 1024.0)
            peak_mem_mb = torch.cuda.max_memory_allocated(device) / (1024.0 * 1024.0)
            msg += f", Mem: {mem_mb:.0f}/{peak_mem_mb:.0f} MB"

        if val_dataloader is not None:
            model.eval()
            val_total = 0.0
            val_count = 0
            with torch.no_grad():
                for inputs, targets in val_dataloader:
                    inputs = inputs.to(device, non_blocking=pin_memory)
                    targets = targets.to(device, non_blocking=pin_memory)
                    with torch.amp.autocast("cuda", enabled=amp_enabled):
                        outputs = model(inputs)
                        loss = criterion(outputs, targets)
                    val_total += loss.item()
                    val_count += 1

            if val_count > 0:
                msg += f", Val Loss: {val_total / val_count:.6f}"
            else:
                msg += ", Val Loss: n/a (no validation batches)"

        print(msg)

    if overfit_n > 0 and last_train_loss is not None:
        near_zero = last_train_loss < 1e-3
        print(
            f"Overfit sanity result: final_train_loss={last_train_loss:.6f}, "
            f"near_zero={'YES' if near_zero else 'NO'}"
        )

    # Export to ONNX
    print("Exporting to ONNX...")
    model.cpu()
    model.eval()
    with torch.no_grad():
        dummy_input = torch.randn(1, INPUT_CHANNELS, BOARD_SIZE, BOARD_SIZE, device="cpu")
        torch.onnx.export(
            model,
            dummy_input,
            MODEL_PATH,
            input_names=["input"],
            output_names=["output"],
            dynamic_axes={"input": {0: "batch_size"}, "output": {0: "batch_size"}},
            opset_version=15,
        )
    print(f"Model saved to {MODEL_PATH}")

    if check_onnx:
        validate_onnx_export(model, MODEL_PATH)


if __name__ == "__main__":
    args = parse_args()
    train(
        data_path=args.data,
        epochs=args.epochs,
        batch_size=args.batch_size,
        device_mode=args.device,
        check_onnx=args.check_onnx,
        num_workers=args.num_workers,
        pin_memory=args.pin_memory,
        prefetch_factor=args.prefetch_factor,
        amp=args.amp,
        val_fraction=args.val_fraction,
        shuffle_buffer=args.shuffle_buffer,
        overfit_n=args.overfit_n,
        strict_metadata=bool(args.strict_metadata),
        skip_bad_games=bool(args.skip_bad_games),
        expected_orth_capture=args.expected_orth_capture,
    )
