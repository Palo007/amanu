"""An offline echo canceller that solves the echo path instead of chasing it.

Amanu has the whole mic track and the whole call track before it starts. So the path is
fitted in closed form: per STFT bin, a multi-tap filter across frames (a convolutive
transfer function) by weighted least squares over the whole file or a segment of it. The
weights are one over the power of what is left after the echo is taken out (my voice and
the noise), found by iteration, so frames and bins where I talk count little and the fit
needs no double-talk detector, no step size and no convergence time.

    out, info = cancel(mic, far)
"""
import numpy as np
from numpy.lib.stride_tricks import sliding_window_view

RATE = 16000


def stft(x, n=1024, hop=256):
    w = np.sqrt(np.hanning(n + 1)[:n])
    x = np.pad(x, (n, n))
    frames = sliding_window_view(x, n)[::hop] * w
    return np.fft.rfft(frames, axis=1).T.astype(np.complex64)          # (bins, frames)


def istft(X, length, n=1024, hop=256):
    w = np.sqrt(np.hanning(n + 1)[:n])
    frames = np.fft.irfft(X.T, n=n, axis=1) * w
    out = np.zeros((X.shape[1] - 1) * hop + n)
    for k in range(n // hop):                                           # overlap-add, one phase at a time
        part = frames[k::n // hop].reshape(-1)
        out[k * hop: k * hop + len(part)] += part
    return out[n: n + length] / (n / hop / 2)


def bulk_delay(mic, far, max_s=0.5):
    """Samples by which the echo in the mic lags the call track: the peak of their
    phase-weighted cross-correlation over the whole file."""
    n = 1 << int(np.ceil(np.log2(len(mic) + int(max_s * RATE))))
    M, F = np.fft.rfft(mic, n), np.fft.rfft(far, n)
    c = M * np.conj(F)
    c = np.fft.irfft(c / (np.abs(c) + 1e-12 * np.max(np.abs(c))), n)
    return int(np.argmax(np.abs(c[: int(max_s * RATE)])))


def fit(Y, X, taps, pre, weights=None, ridge=1e-4, chunk=48):
    """Filters G (bins, pre + taps) with Y[f, t] ~ sum_k G[f, k] X[f, t + pre - k]."""
    bins, frames = Y.shape
    lt = pre + taps
    Xp = np.pad(X, ((0, 0), (taps - 1, pre)))
    A = sliding_window_view(Xp, lt, axis=1)[:, :, ::-1]                 # (bins, frames, lt), a view
    active = np.flatnonzero(np.abs(A).sum(axis=(0, 2)) > 0)             # frames the call track reaches
    G = np.zeros((bins, lt), np.complex64)
    for s in range(0, bins, chunk):
        a = A[s:s + chunk][:, active]
        y = Y[s:s + chunk][:, active]
        if weights is not None:
            sw = np.sqrt(weights[s:s + chunk][:, active])
            a = a * sw[:, :, None]; y = y * sw
        ah = np.conj(np.swapaxes(a, 1, 2))
        R = ah @ a
        p = (ah @ y[:, :, None])[:, :, 0]
        load = ridge * np.real(np.trace(R, axis1=1, axis2=2)) / lt + 1e-12
        R = R + load[:, None, None] * np.eye(lt, dtype=np.complex64)
        G[s:s + chunk] = np.linalg.solve(R, p[:, :, None])[:, :, 0]
    return G


def apply(X, G, pre):
    bins, lt = G.shape
    Xp = np.pad(X, ((0, 0), (lt - pre - 1, pre)))
    A = sliding_window_view(Xp, lt, axis=1)[:, :, ::-1]
    return np.einsum("ftk,fk->ft", A, G, optimize=True)


def smooth(P, t=2, f=1):
    """Mean over 2t+1 frames and 2f+1 bins."""
    c = np.cumsum(np.pad(P, ((0, 0), (t + 1, t)), mode="edge"), axis=1)
    P = (c[:, 2 * t + 1:] - c[:, :-2 * t - 1]) / (2 * t + 1)
    c = np.cumsum(np.pad(P, ((f + 1, f), (0, 0)), mode="edge"), axis=0)
    return (c[2 * f + 1:] - c[:-2 * f - 1]) / (2 * f + 1)


def cancel(mic, far, n=1024, hop=256, tail_s=0.75, pre=2, iterations=3, delay=None, ridge=1e-4, floor_db=-40.0):
    """The mic with the linear echo of `far` taken out. info holds the echo estimate's STFT
    (D), the residual's (E) and the delay."""
    if delay is None:
        delay = bulk_delay(mic, far)
    shift = max(0, delay - hop // 2)                                     # the direct sound lands on the first causal taps
    fd = np.concatenate([np.zeros(shift), far])[: len(mic)]
    Y, X = stft(mic, n, hop), stft(fd, n, hop)
    taps = int(np.ceil(tail_s * RATE / hop))
    w = None
    for _ in range(iterations):
        G = fit(Y, X, taps, pre, w, ridge)
        D = apply(X, G, pre)
        E = Y - D
        lam = smooth(np.abs(E) ** 2)
        lam = np.maximum(lam, 10 ** (floor_db / 10) * lam.mean(axis=1, keepdims=True))
        w = (1.0 / lam).astype(np.float32)
    return istft(E, len(mic), n, hop), {"D": D, "E": E, "Y": Y, "delay": delay, "G": G}


# ------------------------------------------------------------------ delay, drift, device change

def gcc_points(mic, far, win_s, hop_s, max_s=0.6, min_lag=0):
    """Per window: (centre time in s, lag of the echo in samples, to a fraction, peak sharpness).
    The lag is the peak of the phase-weighted cross-correlation between 300 and 6000 Hz."""
    win, hop, maxlag = int(win_s * RATE), int(hop_s * RATE), int(max_s * RATE)
    n = 1 << int(np.ceil(np.log2(win + maxlag)))
    f = np.fft.rfftfreq(n, 1 / RATE)
    band = (f > 300) & (f < 6000)
    level = np.mean(far ** 2) * 0.05
    out = []
    for a in range(0, len(mic) - win + 1, hop):
        x = far[a:a + win]
        if np.mean(x ** 2) < level:
            continue
        C = np.fft.rfft(mic[a:a + win], n) * np.conj(np.fft.rfft(x, n))
        C = np.where(band, C / (np.abs(C) + 1e-20), 0)
        c = np.abs(np.fft.irfft(C, n)[min_lag:maxlag])
        k = int(np.argmax(c))
        frac = 0.0
        if 0 < k < len(c) - 1:
            l, m, r = c[k - 1], c[k], c[k + 1]
            frac = 0.5 * (l - r) / (l - 2 * m + r + 1e-20)
        out.append(((a + win / 2) / RATE, min_lag + k + frac, c[k] / (np.median(c) + 1e-20)))
    return np.array(out).reshape(-1, 3)


def vote(t, d, ppms, tol):
    """The line d = i + s t that the most points lie within tol of, by a vote over slopes
    (ppm) and offsets, then least squares on those points. Returns (i, s, mask of the points)."""
    best = (0, 0.0, 0.0)
    for ppm in ppms:
        s = ppm * 1e-6 * RATE
        r = np.sort(d - s * t)
        hi = np.searchsorted(r, r + 2 * tol, side="right")
        j = int(np.argmax(hi - np.arange(len(r))))
        if hi[j] - j > best[0]:
            best = (hi[j] - j, s, float(np.median(r[j:hi[j]])))
    _, s, i = best
    for _ in range(3):
        near = np.abs(d - (i + s * t)) <= tol
        if near.sum() >= 6 and np.ptp(t[near]) >= 20:
            s, i = np.polyfit(t[near], d[near], 1)
        elif near.any():
            i = float(np.median(d[near] - s * t[near]))
    return float(i), float(s), np.abs(d - (i + s * t)) <= tol


def sections(points, tol, min_points=8):
    """Splits the delay points into stretches that each lie on one line (one device, one clock
    drift). Returns [(t_start, t_end, lag at t = 0 in samples, slope in samples per second)].
    Points that agree with no line (noise, my own voice, a loud reflection) are left out; a
    call with no echo gives no line at all. A second line counts as another device only when
    its points come in one run of five or more that the first line's points do not enclose:
    a reflection of the same room shows up between points of the direct sound."""
    pts = points[points[:, 2] >= 4.0]
    t, d = pts[:, 0], pts[:, 1]
    label = np.full(len(pts), -1)
    found = []
    for k in range(3):
        free = np.flatnonzero(label < 0)
        if len(free) < min_points:
            break
        i, s, near = vote(t[free], d[free], range(-300, 301, 10), tol)
        if near.sum() < max(min_points, 0.2 * len(pts)):
            break
        label[free[near]] = k
        found.append([i, s, int(near.sum()), float(np.ptp(t[free[near]]))])
    if not found:
        return []
    for f in found[1:]:                                         # a short stretch borrows the main line's drift
        if f[2] < 15 or f[3] < 60:
            j = np.flatnonzero(label == found.index(f))
            f[1] = found[0][1]
            f[0] = float(np.median(d[j] - f[1] * t[j]))
    runs = []
    for j in np.flatnonzero(label >= 0):
        if runs and runs[-1][0] == label[j]:
            runs[-1][2] = t[j]; runs[-1][3] += 1
        else:
            runs.append([label[j], t[j], t[j], 1])
    changed = True
    while changed:                                              # drop short runs and runs inside another line's runs
        changed = False
        for k, r in enumerate(runs):
            inside = 0 < k < len(runs) - 1 and runs[k - 1][0] == runs[k + 1][0] and r[3] < runs[k - 1][3] + runs[k + 1][3]
            if len(runs) > 1 and (r[3] < 5 or inside):
                runs.pop(k); changed = True
                break
        merged = []
        for r in runs:
            if merged and merged[-1][0] == r[0]:
                merged[-1][2] = r[2]; merged[-1][3] += r[3]
            else:
                merged.append(r)
        runs = merged
    out = []
    for k, (lj, a, b, _) in enumerate(runs):
        start = 0.0 if k == 0 else (runs[k - 1][2] + a) / 2
        end = np.inf if k == len(runs) - 1 else (b + runs[k + 1][1]) / 2
        out.append((start, end, found[lj][0], found[lj][1]))
    return out


def warp(far, secs, length, lead):
    """The call track as the mic heard it: sample n is far(n - lag(n) + lead), lag from the
    section lines, by windowed-sinc interpolation. `lead` samples of the path stay in."""
    t = np.arange(length) / RATE
    lag = np.zeros(length)
    for a, b, i, s in secs:
        m = (t >= a) & (t < b)
        lag[m] = i + s * t[m]
    pos = np.arange(length) - lag + lead
    taps = 32
    k = np.arange(-taps // 2 + 1, taps // 2 + 1)
    xp = np.pad(far, (taps, taps + 1))
    out = np.zeros(length)
    for s0 in range(0, length, 1 << 18):
        p = pos[s0:s0 + (1 << 18)]
        ok = (p >= 0) & (p < len(far))
        i = np.floor(p[ok]).astype(np.int64); f = p[ok] - i
        arg = k[None, :] - f[:, None]
        w = np.sinc(arg) * (0.5 + 0.5 * np.cos(np.pi * arg / (taps / 2 + 1)))
        seg = np.zeros(len(p))
        seg[ok] = np.einsum("ij,ij->i", xp[i[:, None] + k[None, :] + taps], w)
        out[s0:s0 + len(p)] = seg
    return out


def track(mic, far):
    """The echo's delay over the call: coarse lines from 2 s windows (which a drift of
    200 ppm smears by 6 samples only), then, with the call track warped by them, fine lines
    from 8 s windows."""
    coarse = sections(gcc_points(mic, far, 2.0, 1.0), tol=24.0)
    if not coarse:
        return []
    lead = 1024
    fine_pts = gcc_points(mic, warp(far, coarse, len(mic), lead), 8.0, 4.0, max_s=(2 * lead) / RATE)
    fine = []
    for a, b, i, s in coarse:
        m = (fine_pts[:, 0] >= a) & (fine_pts[:, 0] < b) & (fine_pts[:, 2] >= 4.0)
        t, d = fine_pts[m, 0], fine_pts[m, 1] - lead               # what the coarse line left
        if len(t) >= 3:
            di, ds, _ = vote(t, d, range(-30, 31), 3.0)
        else:
            di, ds = 0.0, 0.0
        fine.append((a, b, i + di, s + ds))
    return fine
