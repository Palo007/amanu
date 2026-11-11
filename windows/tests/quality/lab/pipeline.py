"""Candidate removers built from solve.py and denoise.py, each (mic, far) -> cleaned mic."""
import numpy as np
import solve, denoise

import variants


def solver(floor=None, over=2.0):
    """The offline canceller; with `floor`, also its residual suppressor. A recording that
    joins many devices goes to plain Sonora, as the delay-primed Sonora models do."""
    def run(m, f):
        out, info = solve.cancel(m, f)
        if info["many_paths"]:
            return variants.VARIANTS["sonora"](m, f)
        return solve.suppress(out, info["echo"], floor, over) if floor and info["sections"] else out
    return run


VARIANTS = {
    "solve": solver(),
    "solve-res15": solver(0.15),
    "solve-res30": solver(0.30),
}


def then(first, second):
    return lambda m, f: second(first(m, f))


VARIANTS.update({
    "solve+dn": then(VARIANTS["solve"], denoise.dpdf),
    "solve-res15+dn": then(VARIANTS["solve-res15"], denoise.dpdf),
    "sdf+dn": then(variants.VARIANTS["bw60-floor50"], denoise.dpdf),     # today's best, then the denoiser
})


def speech_gate(x, clean, floor=0.15, below_db=25.0, smooth_s=0.3, least_s=0.3, hang_s=0.3, n=512, hop=256):
    """The gain, per sample, that turns `x` down to `floor` where I do not talk. `clean` is a
    denoiser's output for x; DPDFNet's is close to silent outside the foreground speech, so
    its level, averaged over `smooth_s`, marks my speech: within `below_db` of its 99th
    percentile, for at least `least_s`, kept open `hang_s` on both sides.
    The engine loses my words in loud noise mostly because of the noise in the stretches
    where I am silent (m1.py), not because of the noise over my words."""
    frames = 1 + (len(clean) - n) // hop
    idx = np.arange(n)[None, :] + hop * np.arange(frames)[:, None]
    p = (clean[idx] ** 2).mean(axis=1)
    k = max(1, int(round(smooth_s * solve.RATE / hop)))
    p = np.convolve(np.pad(p, k // 2, mode="edge"), np.ones(k) / k, "valid")[:frames]
    active = p >= np.percentile(p, 99) * 10 ** (-below_db / 10)
    # Drop the short bursts (a voice from the noise that the denoiser let through), then widen.
    edges = np.flatnonzero(np.diff(np.concatenate([[0], active.astype(int), [0]])))
    for a, b in zip(edges[::2], edges[1::2]):
        if (b - a) * hop < least_s * solve.RATE:
            active[a:b] = False
    h = int(round(hang_s * solve.RATE / hop))
    active = np.convolve(active.astype(float), np.ones(2 * h + 1), "same") > 0
    g = np.interp(np.arange(len(x)), hop * np.arange(frames) + n / 2, np.where(active, 1.0, floor))
    ramp = int(0.03 * solve.RATE)
    return np.convolve(np.pad(g, ramp // 2, mode="edge"), np.ones(ramp) / ramp, "valid")[:len(x)]


def gated(first, keep=1.0, **kw):
    """`first`, then the speech gate from DPDFNet's output; `keep` is the share of the
    signal that stays without the denoiser (1 = the denoiser only steers the gate)."""
    def run(m, f):
        e = first(m, f)
        clean = denoise.dpdf(e)
        return speech_gate(e, clean, **kw) * (keep * e + (1 - keep) * clean)
    return run


VARIANTS.update({
    "solve-gate": gated(VARIANTS["solve"]),
    "solve-res15-gate": gated(VARIANTS["solve-res15"]),
    "solve-res15-gate-mix50": gated(VARIANTS["solve-res15"], keep=0.5),
})
