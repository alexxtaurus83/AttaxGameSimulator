"""Two-headed Ataxx network, masked losses and ONNX export with an explicit, validated contract."""

import hashlib
import json

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

import ataxx_common as C

MASK_FILL = -1e9


class AtaxxPolicyValueNet(nn.Module):
    """Plain conv trunk (no BatchNorm: keeps the graph simple for Unity Sentis), a spatial policy head and a
    value head.

    policy head: 1x1 conv to 17 planes of 7x7; flattening gives index kind * 49 + to_square, exactly ActionCodec.
    value head : 1x1 conv to `value_channels`, flatten, MLP, tanh. Flatten (not global average pooling) keeps
                 location information that matters for material and mobility.
    Both heads read the same trunk, so the trunk learns features useful for both.
    """

    def __init__(self, channels=64, layers=5, value_channels=8, value_hidden=64):
        super().__init__()
        if layers < 2:
            raise ValueError("layers must be >= 2")
        self.config = {"channels": channels, "layers": layers, "value_channels": value_channels, "value_hidden": value_hidden}
        convs = [nn.Conv2d(C.CHANNELS, channels, 3, padding=1)]
        convs += [nn.Conv2d(channels, channels, 3, padding=1) for _ in range(layers - 1)]
        self.convs = nn.ModuleList(convs)
        self.policy_conv = nn.Conv2d(channels, C.KINDS, 1)
        self.value_conv = nn.Conv2d(channels, value_channels, 1)
        self.value_fc1 = nn.Linear(value_channels * C.SQUARES, value_hidden)
        self.value_fc2 = nn.Linear(value_hidden, 1)

    def forward(self, x):
        for conv in self.convs:
            x = F.relu(conv(x))
        logits = self.policy_conv(x).flatten(1)            # [N, 17*49], index = kind*49 + square
        v = F.relu(self.value_conv(x)).flatten(1)
        v = F.relu(self.value_fc1(v))
        value = torch.tanh(self.value_fc2(v))              # [N, 1]
        return logits, value


def masked_logits(logits, x):
    """Illegal actions get a huge negative logit; legality comes from the input planes themselves."""
    legal = C.legal_mask_from_planes(x)
    return logits.float().masked_fill(~legal, MASK_FILL), legal


def compute_losses(model_out, batch, policy_weight=1.0, value_weight=1.0):
    """Returns (total, metrics dict). Every term is computed only where its target is valid."""
    logits, value = model_out
    masked, legal = masked_logits(logits, batch["x"])
    device = logits.device
    zero = logits.sum() * 0.0  # keeps the graph connected when a term has no valid rows

    tv = batch["teacher_valid"]
    n_pol = int(tv.sum())
    if n_pol > 0:
        target = batch["teacher"][tv]
        pol_logits = masked[tv]
        policy_loss = F.cross_entropy(pol_logits, target)
        top1 = (pol_logits.argmax(1) == target).float().mean()
    else:
        policy_loss = zero
        top1 = torch.tensor(float("nan"), device=device)

    vv = batch["value_valid"]
    n_val = int(vv.sum())
    if n_val > 0:
        pred = value.float()[vv, 0]
        tgt = batch["value"][vv]
        value_loss = F.mse_loss(pred, tgt)
        nonzero = tgt != 0
        sign_acc = ((torch.sign(pred) == torch.sign(tgt)) & nonzero).sum().float() / nonzero.sum().clamp(min=1)
        # Fraction of the target variance the head explains (1 = perfect, 0 = no better than always predicting the mean, <0 = worse).
        # Sign accuracy only means something for a +/-1 outcome; for a continuous score this is the number to watch.
        var = tgt.var(unbiased=False)
        r2 = 1.0 - ((pred - tgt) ** 2).mean() / var.clamp(min=1e-8)
        # Share of ordinary (non-saturated) positions whose predicted sign is right: the part of the score that is not a decided game.
        ordinary = tgt.abs() < 0.95
        sign_acc_ordinary = ((torch.sign(pred) == torch.sign(tgt)) & ordinary & nonzero).sum().float() / (ordinary & nonzero).sum().clamp(min=1)
        target_var = var
    else:
        value_loss = zero
        sign_acc = torch.tensor(float("nan"), device=device)
        r2 = torch.tensor(float("nan"), device=device)
        sign_acc_ordinary = torch.tensor(float("nan"), device=device)
        target_var = torch.tensor(float("nan"), device=device)

    total = policy_weight * policy_loss + value_weight * value_loss
    metrics = {
        "policy_loss": float(policy_loss.detach()),
        "value_loss": float(value_loss.detach()),
        "policy_top1": float(top1),
        "value_sign_acc": float(sign_acc),
        "value_r2": float(r2),
        "value_sign_acc_ordinary": float(sign_acc_ordinary),
        "value_target_var": float(target_var),
        "n_policy": n_pol,
        "n_value": n_val,
    }
    return total, metrics


# ---------------------------------------------------------------------------------------------------------
# ONNX export and validation
# ---------------------------------------------------------------------------------------------------------

def contract_metadata(model, extra=None):
    meta = {
        "contract": C.CONTRACT_ID,
        "board_size": str(C.BOARD_SIZE),
        "input_channels": str(C.CHANNELS),
        "input_layout": "NCHW friendly,enemy,blocked,constant; side-to-move perspective",
        "action_count": str(C.ACTION_COUNT),
        "action_encoding": "kind*49+to_square; kind0=clone, kind1..16=jump from to+offset",
        "policy_output": "unmasked logits; mask illegal actions before selection",
        "value_output": "[-1,1] expected outcome for side to move",
        "arch": json.dumps(model.config, sort_keys=True),
    }
    if extra:
        meta.update({k: str(v) for k, v in extra.items()})
    return meta


def export_onnx(model, path, metadata=None):
    import onnx

    model = model.to("cpu").eval()
    dummy = torch.zeros(1, C.CHANNELS, C.BOARD_SIZE, C.BOARD_SIZE)
    torch.onnx.export(
        model,
        dummy,
        path,
        input_names=[C.INPUT_NAME],
        output_names=[C.OUTPUT_POLICY, C.OUTPUT_VALUE],
        dynamic_axes={C.INPUT_NAME: {0: "batch"}, C.OUTPUT_POLICY: {0: "batch"}, C.OUTPUT_VALUE: {0: "batch"}},
        opset_version=C.ONNX_OPSET,
        do_constant_folding=True,
    )
    m = onnx.load(path)
    del m.metadata_props[:]
    for k, v in contract_metadata(model, metadata).items():
        p = m.metadata_props.add()
        p.key, p.value = k, v
    onnx.checker.check_model(m)
    onnx.save(m, path)


def file_sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def validate_onnx(model, path, batch=32, atol=1e-4, seed=0):
    """Checks names, shapes, finiteness, value range and torch/ONNX-Runtime numerical parity."""
    import onnx
    import onnxruntime as ort

    m = onnx.load(path)
    onnx.checker.check_model(m)
    meta = {p.key: p.value for p in m.metadata_props}
    if meta.get("contract") != C.CONTRACT_ID:
        raise AssertionError(f"ONNX contract metadata is {meta.get('contract')!r}, expected {C.CONTRACT_ID!r}")

    sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    ins = {i.name: i for i in sess.get_inputs()}
    outs = {o.name: o for o in sess.get_outputs()}
    if list(ins) != [C.INPUT_NAME]:
        raise AssertionError(f"inputs are {list(ins)}, expected [{C.INPUT_NAME!r}]")
    if list(outs) != [C.OUTPUT_POLICY, C.OUTPUT_VALUE]:
        raise AssertionError(f"outputs are {list(outs)}, expected [{C.OUTPUT_POLICY!r}, {C.OUTPUT_VALUE!r}]")
    if list(ins[C.INPUT_NAME].shape[1:]) != [C.CHANNELS, C.BOARD_SIZE, C.BOARD_SIZE]:
        raise AssertionError(f"input shape {ins[C.INPUT_NAME].shape}")

    rng = np.random.default_rng(seed)
    x = (rng.random((batch, C.CHANNELS, C.BOARD_SIZE, C.BOARD_SIZE)) < 0.3).astype(np.float32)
    x[:, 3] = 1.0
    with torch.no_grad():
        t_logits, t_value = model.to("cpu").eval()(torch.from_numpy(x))
    o_logits, o_value = sess.run([C.OUTPUT_POLICY, C.OUTPUT_VALUE], {C.INPUT_NAME: x})

    if o_logits.shape != (batch, C.ACTION_COUNT) or o_value.shape != (batch, 1):
        raise AssertionError(f"output shapes {o_logits.shape}, {o_value.shape}")
    if not (np.isfinite(o_logits).all() and np.isfinite(o_value).all()):
        raise AssertionError("ONNX outputs contain non-finite values")
    if np.abs(o_value).max() > 1.0 + 1e-6:
        raise AssertionError("ONNX value output is outside [-1, 1]")
    d_pol = float(np.abs(t_logits.numpy() - o_logits).max())
    d_val = float(np.abs(t_value.numpy() - o_value).max())
    if d_pol > atol or d_val > atol:
        raise AssertionError(f"torch/ONNX mismatch: policy {d_pol:.3e}, value {d_val:.3e} (atol {atol})")
    # A batch of one must match the same row inside a batch (dynamic batch axis really is dynamic).
    o1_logits, o1_value = sess.run([C.OUTPUT_POLICY, C.OUTPUT_VALUE], {C.INPUT_NAME: x[:1]})
    if float(np.abs(o1_logits - o_logits[:1]).max()) > atol or float(np.abs(o1_value - o_value[:1]).max()) > atol:
        raise AssertionError("ONNX output depends on batch size")
    return {"max_abs_diff_policy": d_pol, "max_abs_diff_value": d_val}
