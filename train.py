"""Train the two-headed Ataxx policy/value network from v3 logs and export a versioned ONNX artifact.

    python train.py --data logs/ --out models/gen1 --epochs 20

Outputs in --out:
    model.onnx        contract attax-pv-1 (inputs: board; outputs: policy_logits, value), validated against torch
    checkpoint.pt     resumable state (model, optimizer, epoch, RNG, config); used by --resume and --init-from
    report.json       data statistics, per-epoch losses/metrics, timings, artifact hash
Nothing is overwritten silently: a non-empty --out is refused unless --resume or --overwrite is given.
"""

import argparse
import json
import math
import os
import shutil
import time

import numpy as np
import torch

import ataxx_common as C
import ataxx_data as D
import ataxx_gpu as G
import ataxx_model as M


def parse_args(argv=None):
    p = argparse.ArgumentParser(description="Train the Ataxx policy/value network")
    p.add_argument("--data", nargs="+", required=True, help="v3 .bin files and/or directories")
    p.add_argument("--out", required=True, help="output directory for this model generation")
    p.add_argument("--epochs", type=int, default=10)
    p.add_argument("--batch-size", type=int, default=512)
    p.add_argument("--lr", type=float, default=1e-3)
    p.add_argument("--lr-schedule", choices=["constant", "cosine"], default="constant",
                   help="cosine decays lr from --lr to --lr * --lr-min-ratio over --epochs (per optimizer step; stateless, so --resume "
                        "is exact only if --epochs is unchanged)")
    p.add_argument("--lr-min-ratio", type=float, default=0.02)
    p.add_argument("--weight-decay", type=float, default=1e-4)
    p.add_argument("--policy-weight", type=float, default=1.0)
    p.add_argument("--value-weight", type=float, default=1.0)
    p.add_argument("--value-target", choices=list(D.VALUE_MODES), default="score",
                   help="what the value head learns: score = tanh(teacher search score / --score-scale) (default; needs labels written by this version of "
                        "selfplay/relabel), outcome = the played game's result (noisy), mix = --value-mix * score + (1 - --value-mix) * outcome")
    p.add_argument("--score-scale", type=float, default=D.DEFAULT_SCORE_SCALE,
                   help="tanh scale for the score target, in engine units (heuristic points x 10,000). Ordinary positions are within about +/-1e7; "
                        "the default 4e6 keeps them in the steep part of tanh and saturates decisive wins/losses to +/-1")
    p.add_argument("--value-mix", type=float, default=0.5, help="weight of the score target in --value-target mix")
    p.add_argument("--channels", type=int, default=64)
    p.add_argument("--layers", type=int, default=5)
    p.add_argument("--val-fraction", type=float, default=0.05, help="validation share of GAMES (by stable game uid)")
    p.add_argument("--include-plycap", action="store_true", help="also use outcomes of ply-cap games (piece-count fallback, not a real result)")
    p.add_argument("--no-augment", action="store_true", help="disable random 8-fold symmetry augmentation")
    p.add_argument("--device", choices=["auto", "cpu", "cuda"], default="auto")
    p.add_argument("--amp", action="store_true", help="mixed precision on CUDA")
    p.add_argument("--seed", type=int, default=0)
    p.add_argument("--max-samples", type=int, default=0, help="debug: stop loading after N samples")
    p.add_argument("--resume", action="store_true", help="continue from <out>/checkpoint.pt")
    p.add_argument("--init-from", default=None, help="warm start weights from another checkpoint.pt (fresh optimizer, epoch 0)")
    p.add_argument("--overwrite", action="store_true", help="allow reusing a non-empty --out")
    p.add_argument("--keep-best", action="store_true", help="export the epoch with the best validation loss instead of the last")
    p.add_argument("--overfit-n", type=int, default=0, help="sanity mode: train on the first N samples only, no validation")
    p.add_argument("--no-validate-onnx", action="store_true")
    p.add_argument("--keep-unlabeled", action="store_true", help="keep rows that have neither a policy label nor a value target (they train nothing and only cost time)")
    p.add_argument("--gpu-log", type=float, default=0.0,
                   help="log GPU utilisation / memory / temperature / power every N seconds via NVML (0 = off, no thread started). "
                        "Needs `pip install nvidia-ml-py`; disables itself if unavailable. Per-epoch GPU stats and PyTorch's own peak "
                        "memory (free counter reads) go to report.json.")
    return p.parse_args(argv)


def lr_at(base, schedule, min_ratio, progress):
    """Learning rate at training progress in [0, 1]."""
    if schedule == "constant":
        return base
    progress = min(1.0, max(0.0, progress))
    return base * (min_ratio + (1.0 - min_ratio) * 0.5 * (1.0 + math.cos(math.pi * progress)))


def resolve_device(mode):
    if mode == "cpu":
        return torch.device("cpu")
    if mode == "cuda":
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA requested but not available")
        return torch.device("cuda")
    return torch.device("cuda" if torch.cuda.is_available() else "cpu")


@torch.no_grad()
def evaluate(model, data, batch_size, device, weights):
    model.eval()
    agg = {"loss": 0.0, "policy_loss": 0.0, "value_loss": 0.0, "n_policy": 0, "n_value": 0, "top1_hits": 0.0, "sign_hits": 0.0, "sign_n": 0,
           "t_sum": 0.0, "t_sq": 0.0, "se": 0.0, "ord_hits": 0.0, "ord_n": 0}
    for start in range(0, data.n, batch_size):
        idx = torch.arange(start, min(data.n, start + batch_size), device=device)
        b = data.batch(idx, augment=False)
        out = model(b["x"])
        _, m = M.compute_losses(out, b, *weights)
        agg["policy_loss"] += m["policy_loss"] * m["n_policy"]
        agg["value_loss"] += m["value_loss"] * m["n_value"]
        agg["n_policy"] += m["n_policy"]
        agg["n_value"] += m["n_value"]
        if m["n_policy"]:
            agg["top1_hits"] += m["policy_top1"] * m["n_policy"]
        vv = b["value_valid"]
        tgt = b["value"][vv]
        nz = int((tgt != 0).sum())
        if nz and m["n_value"]:
            agg["sign_hits"] += m["value_sign_acc"] * nz
            agg["sign_n"] += nz
        if m["n_value"]:
            # exact global sums so R^2 uses the variance of the whole validation set, not an average of per-batch variances
            pred = out[1].float()[vv, 0].double(); td = tgt.double()
            agg["t_sum"] += float(td.sum()); agg["t_sq"] += float((td * td).sum()); agg["se"] += float(((pred - td) ** 2).sum())
            ordn = (td.abs() < 0.95) & (td != 0)
            agg["ord_hits"] += float(((torch.sign(pred) == torch.sign(td)) & ordn).sum()); agg["ord_n"] += int(ordn.sum())
    res = {
        "policy_loss": agg["policy_loss"] / agg["n_policy"] if agg["n_policy"] else None,
        "value_loss": agg["value_loss"] / agg["n_value"] if agg["n_value"] else None,
        "policy_top1": agg["top1_hits"] / agg["n_policy"] if agg["n_policy"] else None,
        "value_sign_acc": agg["sign_hits"] / agg["sign_n"] if agg["sign_n"] else None,
        "value_r2": None,
        "value_sign_acc_ordinary": agg["ord_hits"] / agg["ord_n"] if agg["ord_n"] else None,
        "n_policy": agg["n_policy"],
        "n_value": agg["n_value"],
    }
    if agg["n_value"]:
        mean = agg["t_sum"] / agg["n_value"]
        var = agg["t_sq"] / agg["n_value"] - mean * mean
        res["value_r2"] = 1.0 - (agg["se"] / agg["n_value"]) / max(var, 1e-8)
        res["value_target_var"] = var
    parts = []
    if res["policy_loss"] is not None:
        parts.append(weights[0] * res["policy_loss"])
    if res["value_loss"] is not None:
        parts.append(weights[1] * res["value_loss"])
    res["loss"] = float(sum(parts)) if parts else None
    return res


def main(argv=None):
    args = parse_args(argv)
    torch.manual_seed(args.seed)
    np.random.seed(args.seed)
    device = resolve_device(args.device)
    os.makedirs(args.out, exist_ok=True)
    ckpt_path = os.path.join(args.out, "checkpoint.pt")
    if os.listdir(args.out) and not (args.resume or args.overwrite):
        raise SystemExit(f"--out {args.out} is not empty; use --resume to continue or --overwrite to reuse it")
    if args.resume and not os.path.exists(ckpt_path):
        raise SystemExit(f"--resume given but {ckpt_path} does not exist")

    t0 = time.perf_counter()
    train_ds, val_ds = D.load_logs(args.data, args.val_fraction, args.include_plycap, max_samples=args.max_samples)
    load_s = time.perf_counter() - t0
    stats = dict(train_ds.stats)
    print(f"[data] {stats} loaded+validated in {load_s:.1f}s", flush=True)
    if args.overfit_n > 0:
        train_ds = train_ds.subset(np.arange(len(train_ds)) < args.overfit_n)
        val_ds = None
    if int(train_ds.a["teacher_valid"].sum()) == 0 and int(train_ds.a["value_valid"].sum()) == 0 and int(train_ds.a["score_valid"].sum()) == 0:
        raise SystemExit("No usable training targets (no valid teacher labels and no valid outcomes)")

    tdargs = dict(value_mode=args.value_target, score_scale=args.score_scale, value_mix=args.value_mix)
    train = D.TensorData(train_ds, device, **tdargs)
    val = D.TensorData(val_ds, device, **tdargs) if val_ds is not None and len(val_ds) > 0 else None
    if not args.keep_unlabeled:
        # A row with no policy label and no value target contributes exactly nothing to the loss, but still costs a forward/backward pass
        # every epoch. Logs that were only partly relabelled (for example 20% of positions) are mostly such rows.
        train, dropped_train = train.drop_untargeted()
        if val is not None:
            val, _ = val.drop_untargeted()
        print(f"[data] dropped {dropped_train} of {dropped_train + train.n} training rows that have no policy label and no value target "
              f"(--keep-unlabeled to keep them); training on {train.n}", flush=True)
        if train.n == 0:
            raise SystemExit("No rows with a policy label or a value target are left to train on")
    n_value_rows = int(train.value_valid.sum())
    print(f"[value] target={args.value_target} scale={args.score_scale:g} rows with a value target: {n_value_rows} of {train.n}", flush=True)
    if args.value_weight > 0 and n_value_rows == 0:
        raise SystemExit(f"--value-target {args.value_target} has no usable rows in this data (score labels need logs written by the current "
                         f"selfplay/relabel; use --value-target outcome for older data, or --value-weight 0 to train the policy only)")
    weights = (args.policy_weight, args.value_weight)

    model = M.AtaxxPolicyValueNet(channels=args.channels, layers=args.layers).to(device)
    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=args.weight_decay)
    scaler = torch.amp.GradScaler("cuda", enabled=args.amp and device.type == "cuda")
    start_epoch, history, best = 0, [], None

    if args.resume:
        ck = torch.load(ckpt_path, map_location=device, weights_only=False)
        if ck["config"] != model.config:
            raise SystemExit(f"Checkpoint architecture {ck['config']} differs from requested {model.config}")
        model.load_state_dict(ck["model"])
        opt.load_state_dict(ck["optimizer"])
        start_epoch, history, best = ck["epoch"], ck["history"], ck.get("best")
        torch.set_rng_state(ck["torch_rng"].cpu())
        print(f"[resume] continuing from epoch {start_epoch}", flush=True)
    elif args.init_from:
        ck = torch.load(args.init_from, map_location=device, weights_only=False)
        if ck["config"] != model.config:
            raise SystemExit(f"--init-from architecture {ck['config']} differs from requested {model.config}")
        model.load_state_dict(ck["model"])
        print(f"[init] warm start from {args.init_from}", flush=True)

    gen = torch.Generator(device=device)
    gen.manual_seed(args.seed + start_epoch)
    train_seconds = 0.0
    if device.type == "cuda":
        torch.cuda.reset_peak_memory_stats(device)
    # Optional NVML telemetry on its own thread (off unless --gpu-log N); never touches the training loop.
    gpu = G.GpuMonitor(args.gpu_log if device.type == "cuda" else 0.0, label="train", log=lambda s: print(s, flush=True))

    for epoch in range(start_epoch, args.epochs):
        model.train()
        t1 = time.perf_counter()
        perm = torch.randperm(train.n, device=device, generator=gen)
        sums = {"policy_loss": 0.0, "value_loss": 0.0, "n_policy": 0, "n_value": 0}
        steps_per_epoch = (train.n + args.batch_size - 1) // args.batch_size
        for step_i, start in enumerate(range(0, train.n, args.batch_size)):
            lr = lr_at(args.lr, args.lr_schedule, args.lr_min_ratio, (epoch * steps_per_epoch + step_i) / (args.epochs * steps_per_epoch))
            for group in opt.param_groups:
                group["lr"] = lr
            idx = perm[start:start + args.batch_size]
            b = train.batch(idx, augment=not args.no_augment, generator=gen)
            opt.zero_grad(set_to_none=True)
            with torch.autocast(device_type=device.type, enabled=args.amp and device.type == "cuda"):
                out = model(b["x"])
            loss, m = M.compute_losses(out, b, *weights)
            if not torch.isfinite(loss):
                raise RuntimeError(f"Non-finite loss at epoch {epoch + 1}, batch starting {start}: {m}")
            scaler.scale(loss).backward()
            scaler.step(opt)
            scaler.update()
            sums["policy_loss"] += m["policy_loss"] * m["n_policy"]
            sums["value_loss"] += m["value_loss"] * m["n_value"]
            sums["n_policy"] += m["n_policy"]
            sums["n_value"] += m["n_value"]
        if device.type == "cuda":
            torch.cuda.synchronize()
        epoch_s = time.perf_counter() - t1
        train_seconds += epoch_s

        rec = {
            "epoch": epoch + 1,
            "seconds": epoch_s,
            "samples_per_s": train.n / epoch_s,
            "train_policy_loss": sums["policy_loss"] / sums["n_policy"] if sums["n_policy"] else None,
            "train_value_loss": sums["value_loss"] / sums["n_value"] if sums["n_value"] else None,
        }
        if val is not None:
            rec["val"] = evaluate(model, val, args.batch_size, device, weights)
        if gpu.enabled or gpu.summary():
            rec["gpu"] = gpu.take_window()
        history.append(rec)
        print(f"[epoch {epoch + 1}/{args.epochs}] {json.dumps(rec)}", flush=True)

        v = rec.get("val", {}).get("loss") if val is not None else None
        if v is not None and (best is None or v < best["val_loss"]):
            best = {"epoch": epoch + 1, "val_loss": v, "state": {k: t.detach().cpu().clone() for k, t in model.state_dict().items()}}
        torch.save(
            {"model": model.state_dict(), "optimizer": opt.state_dict(), "epoch": epoch + 1, "history": history, "best": best,
             "config": model.config, "torch_rng": torch.get_rng_state(), "args": vars(args)},
            ckpt_path + ".tmp",
        )
        os.replace(ckpt_path + ".tmp", ckpt_path)

    if args.keep_best and best is not None:
        model.load_state_dict(best["state"])
        print(f"[export] using best epoch {best['epoch']} (val loss {best['val_loss']:.5f})", flush=True)

    gpu.close()
    onnx_path = os.path.join(args.out, "model.onnx")
    M.export_onnx(model, onnx_path, {"trained_epochs": len(history), "train_samples": len(train_ds), "data_files": len(args.data)})
    parity = None if args.no_validate_onnx else M.validate_onnx(model, onnx_path)
    report = {
        "data": stats,
        "args": vars(args),
        "model_config": model.config,
        "history": history,
        "train_seconds": train_seconds,
        "load_seconds": load_s,
        "onnx": onnx_path,
        "onnx_sha256": M.file_sha256(onnx_path),
        "onnx_parity": parity,
        # Free counters (no NVML): this process's own peak GPU memory, comparable to the card's total.
        "torch_memory": G.torch_memory_stats(device),
        "gpu_summary": gpu.summary(),
    }
    with open(os.path.join(args.out, "report.json"), "w") as f:
        json.dump(report, f, indent=2, default=str)
    print(f"[done] {onnx_path} sha256={report['onnx_sha256'][:16]} train={train_seconds:.1f}s parity={parity}", flush=True)
    return report


if __name__ == "__main__":
    main()
