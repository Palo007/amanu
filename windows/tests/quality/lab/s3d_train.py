"""Track S3: the distilled speech gate. A convolution and a 2-layer bidirectional GRU on log-mel frames (the
frames of pipeline.speech_gate: 512 samples, hop 256) that predicts how many dB DPDFNet keeps of the input
in each frame. The gate arithmetic of pipeline.speech_gate then runs on (input level + predicted kept dB).

    py -P s3d_train.py train <seconds> [tag]   2 threads, checkpoint every 5 min, resumes
    py -P s3d_train.py val [tag]               disagreement of the 16 ms active flag on the validation mixes
    py -P s3d_train.py export [tag]            ONNX file
"""
import os, sys, time, glob
HOME_ = os.path.join(os.path.expanduser("~"), "Amanu Quality Set", "lab", "overnight")
sys.path.insert(0, os.path.join(HOME_, "N5", "pylib"))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8")
from pathlib import Path
import numpy as np
import torch
from torch import nn

S3 = Path(HOME_) / "S3"
RATE, HOP = 16000, 256
NMEL = 64
WIN = 1500
KMIN, KMAX = -70.0, 5.0


class Gate(nn.Module):
    def __init__(self, c=96, h=64):
        super().__init__()
        self.conv = nn.Conv1d(NMEL + 1, c, 5, padding=2)
        self.gru = nn.GRU(c, h, num_layers=2, batch_first=True, bidirectional=True)
        self.out = nn.Linear(2 * h, 1)

    def forward(self, f, e):
        """f [B,T,64] log-mel in [-4,3]; e [B,T] frame level re its 95th percentile, in dB. Returns kept dB [B,T]."""
        x = torch.cat([f, (e / 20.0).unsqueeze(-1)], -1).transpose(1, 2)
        y = torch.relu(self.conv(x)).transpose(1, 2)
        y, _ = self.gru(y)
        return self.out(y).squeeze(-1) * 20.0


def nparams(m): return sum(p.numel() for p in m.parameters())


def load(split):
    out = []
    for f in sorted(glob.glob(str(S3 / "data" / f"{split}-*.npz"))):
        if f.endswith(".tmp.npz"):
            continue
        z = np.load(f)
        out.append((z["f"], z["e"].astype(np.float32), z["k"].astype(np.float32), float(z["snr"])))
    return out


def tensors(item):
    f, e, k, _ = item
    return torch.from_numpy(f.astype(np.float32) / 30.0), torch.from_numpy(e), torch.from_numpy(k)


def batch(data, bsz, rng):
    F, E, K = [], [], []
    for _ in range(bsz):
        f, e, k, _ = data[int(rng.integers(len(data)))]
        a = int(rng.integers(0, len(e) - WIN))
        F.append(f[a:a + WIN]); E.append(e[a:a + WIN]); K.append(k[a:a + WIN])
    return (torch.from_numpy(np.stack(F).astype(np.float32) / 30.0), torch.from_numpy(np.stack(E)), torch.from_numpy(np.stack(K)))


def loss_fn(pred, e, k):
    t = k.clamp(KMIN, KMAX)
    w = 1.0 + 2.0 * ((e + k) > -40.0).float()
    w = w * torch.where(e > -45.0, torch.ones_like(e), torch.full_like(e, 0.1))
    return (nn.functional.smooth_l1_loss(pred / 10.0, t / 10.0, reduction="none") * w).sum() / w.sum()


def gate_active(pdb, least_s=0.3, hang_s=0.3, smooth_s=0.3, below_db=25.0):
    """The `active` flags of pipeline.speech_gate from per-frame power in dB (any common offset)."""
    p = 10 ** (np.asarray(pdb, np.float64) / 10)
    frames = len(p)
    k = max(1, int(round(smooth_s * RATE / HOP)))
    p = np.convolve(np.pad(p, k // 2, mode="edge"), np.ones(k) / k, "valid")[:frames]
    active = p >= np.percentile(p, 99) * 10 ** (-below_db / 10)
    edges = np.flatnonzero(np.diff(np.concatenate([[0], active.astype(int), [0]])))
    for a, b in zip(edges[::2], edges[1::2]):
        if (b - a) * HOP < least_s * RATE:
            active[a:b] = False
    h = int(round(hang_s * RATE / HOP))
    return np.convolve(active.astype(float), np.ones(2 * h + 1), "same") > 0


def predict(model, f, e, piece=1875, ctx=125):
    """Whole call in pieces of 30 s with 2 s of context, as the app's gate cuts its audio."""
    out = np.zeros(len(e), np.float32)
    model.eval()
    with torch.no_grad():
        for a in range(0, len(e), piece):
            lo, hi = max(0, a - ctx), min(len(e), a + piece + ctx)
            y = model(torch.from_numpy(f[lo:hi].astype(np.float32))[None], torch.from_numpy(e[lo:hi].astype(np.float32))[None])[0].numpy()
            out[a:a + piece] = y[a - lo: a - lo + min(piece, len(e) - a)]
    return out


def val(model, data):
    dis, tot = 0, 0
    per = []
    for it in data:
        f, e, k, snr = it
        f = f.astype(np.float32) / 30.0
        kp = np.clip(predict(model, f, e), KMIN, KMAX)
        a = gate_active(e + k.astype(np.float32))
        b = gate_active(e + kp)
        dis += int((a != b).sum()); tot += len(a)
        per.append((snr, float((a != b).mean()), float(a.mean())))
    return dis / max(tot, 1), per


def ckpt_path(tag): return S3 / "ckpt" / f"{tag}.pt"


def train(total_s, tag="a"):
    torch.set_num_threads(2)
    data, vdata = load("train"), load("val")
    print(f"{len(data)} train mixes, {len(vdata)} validation mixes", flush=True)
    m = Gate()
    print("parameters", nparams(m), flush=True)
    opt = torch.optim.AdamW(m.parameters(), lr=2e-3, weight_decay=1e-4)
    t0, step, spent = time.time(), 0, 0.0
    cp = ckpt_path(tag)
    if cp.exists():
        z = torch.load(cp, weights_only=False)
        m.load_state_dict(z["m"]); opt.load_state_dict(z["o"]); step, spent = z["step"], z["spent"]
        print("resumed at step", step, "after", spent, "s", flush=True)
    cp.parent.mkdir(exist_ok=True)
    rng = np.random.default_rng(step + 1)
    last_ck, ema = time.time(), None
    base = spent
    while True:
        spent = base + time.time() - t0
        if spent > total_s:
            break
        lr = 1e-4 + (2e-3 - 1e-4) * 0.5 * (1 + np.cos(np.pi * min(1.0, spent / total_s)))
        for g in opt.param_groups: g["lr"] = lr
        m.train()
        f, e, k = batch(data, 12, rng)
        loss = loss_fn(m(f, e), e, k)
        opt.zero_grad(); loss.backward(); nn.utils.clip_grad_norm_(m.parameters(), 1.0); opt.step()
        step += 1
        ema = float(loss.detach()) if ema is None else 0.98 * ema + 0.02 * float(loss.detach())
        if step % 50 == 0:
            print(f"step {step} t {spent:.0f} s lr {lr:.2e} loss {ema:.4f}", flush=True)
        if time.time() - last_ck > 300:
            torch.save({"m": m.state_dict(), "o": opt.state_dict(), "step": step, "spent": spent}, cp)
            d, _ = val(m, vdata)
            print(f"  VAL step {step} active-flag disagreement {100 * d:.2f} %", flush=True)
            last_ck = time.time()
    torch.save({"m": m.state_dict(), "o": opt.state_dict(), "step": step, "spent": total_s}, cp)
    d, per = val(m, vdata)
    print(f"final VAL disagreement {100 * d:.2f} %", [(round(s, 1), round(100 * x, 1)) for s, x, _ in per], flush=True)


def load_model(tag="a"):
    m = Gate()
    m.load_state_dict(torch.load(ckpt_path(tag), weights_only=False)["m"])
    return m.eval()


def export(tag="a"):
    m = load_model(tag)
    f = torch.zeros(1, 1875, NMEL); e = torch.zeros(1, 1875)
    out = S3 / f"gate-{tag}.onnx"
    torch.onnx.export(m, (f, e), str(out), input_names=["mel", "level"], output_names=["kept_db"],
                      dynamic_axes={"mel": {0: "b", 1: "t"}, "level": {0: "b", 1: "t"}, "kept_db": {0: "b", 1: "t"}}, opset_version=17, dynamo=False)
    print(out, out.stat().st_size)


if __name__ == "__main__":
    c = sys.argv[1]
    tag = sys.argv[3] if c == "train" and len(sys.argv) > 3 else (sys.argv[2] if c != "train" and len(sys.argv) > 2 else "a")
    if c == "train": train(float(sys.argv[2]), tag)
    elif c == "val":
        d, per = val(load_model(tag), load("val")); print(f"{100 * d:.2f} %", per)
    elif c == "export": export(tag)
