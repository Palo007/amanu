"""Echo removers under test, each a function (mic, far) -> cleaned mic."""
import numpy as np
import sonora, localvqe

def fb(b): return f"filter.refined.length_blocks={b};filter.coarse.length_blocks={b};filter.refined_initial.length_blocks={b};filter.coarse_initial.length_blocks={b}"
EXT = "delay.use_external_delay_estimator=true"
NEAREND = ("suppressor.dominant_nearend_detection.enr_threshold=1;suppressor.dominant_nearend_detection.snr_threshold=10"
           ";suppressor.dominant_nearend_detection.trigger_threshold=4;suppressor.dominant_nearend_detection.hold_duration=100"
           ";suppressor.nearend_tuning.mask_lf.enr_transparent=1.5;suppressor.nearend_tuning.mask_lf.enr_suppress=3"
           ";suppressor.nearend_tuning.mask_hf.enr_transparent=0.5;suppressor.nearend_tuning.mask_hf.enr_suppress=1.5")

def sonora_ext(tuning, passes=1, delay="prime"):
    def run(mic, far):
        shift = localvqe.prime(mic, far) if delay == "prime" else delay
        ms = max(0, shift) // 16 if shift is not None and shift >= 0 else None
        t = tuning + (";" + EXT if ms is not None else "")
        if passes == 1:
            return sonora.run(mic, far, t, stream_delay_ms=ms, stats=False)[0]
        m2 = np.concatenate([mic] * passes); f2 = np.concatenate([far] * passes)
        out = sonora.run(m2, f2, t, stream_delay_ms=ms, stats=False)[0]
        return out[(passes - 1) * len(mic):]
    return run

VARIANTS = {
    "sonora": lambda m, f: sonora.run(m, f, None, stats=False)[0],
    "fast": lambda m, f: localvqe.run(m, f, "fast")[0],
    "ext160": sonora_ext(fb(40)),
    "ext256": sonora_ext(fb(64)),
    "ext256-2pass": sonora_ext(fb(64), 2),
    "ext256-2pass-linear": sonora_ext(fb(64) + ";output=linear", 2),
    "ext256-2pass-nearend": sonora_ext(fb(64) + ";" + NEAREND, 2),
}

def sonora_warm(tuning, warm_s=60):
    """External delay, and the first warm_s seconds run once before the whole file."""
    def run(mic, far):
        shift = localvqe.prime(mic, far)
        ms = shift // 16 if shift >= 0 else None
        t = tuning + (";" + EXT if ms is not None else "")
        w = min(len(mic), warm_s * 16000) // 160 * 160
        out = sonora.run(np.concatenate([mic[:w], mic]), np.concatenate([far[:w], far]), t, stream_delay_ms=ms, stats=False)[0]
        return out[w:]
    return run

GENTLE = NEAREND + (";suppressor.normal_tuning.mask_lf.enr_transparent=0.6;suppressor.normal_tuning.mask_lf.enr_suppress=0.8"
                    ";suppressor.normal_tuning.mask_hf.enr_transparent=0.14;suppressor.normal_tuning.mask_hf.enr_suppress=0.2")
VARIANTS.update({
    "ext160-2pass-linear": sonora_ext(fb(40) + ";output=linear", 2),
    "ext256-warm60-linear": sonora_warm(fb(64) + ";output=linear", 60),
    "ext256-warm60": sonora_warm(fb(64), 60),
    "ext256-2pass-len0": sonora_ext(fb(64) + ";ep_strength.default_len=0;ep_strength.nearend_len=0", 2),
    "ext256-2pass-gentle": sonora_ext(fb(64) + ";" + GENTLE, 2),
    "ext256-2pass-gentle-len0": sonora_ext(fb(64) + ";" + GENTLE + ";ep_strength.default_len=0;ep_strength.nearend_len=0", 2),
})

FLOOR = lambda f: f";suppressor.amanu_gain_floor={f}"
VARIANTS.update({
    "ext256-warm60-floor25": sonora_warm(fb(64) + FLOOR(0.25), 60),
    "ext256-warm60-floor35": sonora_warm(fb(64) + FLOOR(0.35), 60),
    "ext256-warm60-floor50": sonora_warm(fb(64) + FLOOR(0.5), 60),
    "ext256-warm60-nearend-floor50": sonora_warm(fb(64) + ";" + NEAREND + FLOOR(0.5), 60),
    "ext256-warm60-nearend": sonora_warm(fb(64) + ";" + NEAREND, 60),
})

def sonora_warmfar(tuning, seconds=60, repeats=1, threshold=1e-5):
    """External delay; before the stream, AEC3 runs on the first `seconds` of 10 ms frames in
    which the far end plays (taken from anywhere in the file, in order), `repeats` times."""
    def run(mic, far):
        shift = localvqe.prime(mic, far)
        ms = shift // 16 if shift >= 0 else None
        t = tuning + (";" + EXT if ms is not None else "")
        n = len(far) // 160
        p = (far[: n * 160].reshape(n, 160) ** 2).mean(axis=1)
        active = np.flatnonzero(p > threshold)[: seconds * 100]
        idx = (active[:, None] * 160 + np.arange(160)).ravel()
        wm = np.tile(mic[idx], repeats); wf = np.tile(far[idx], repeats)
        out = sonora.run(np.concatenate([wm, mic]), np.concatenate([wf, far]), t, stream_delay_ms=ms, stats=False)[0]
        return out[len(wm):]
    return run

VARIANTS.update({
    "wf60-linear": sonora_warmfar(fb(64) + ";output=linear", 60, 1),
    "wf60x2-linear": sonora_warmfar(fb(64) + ";output=linear", 60, 2),
    "wf60x2-floor50": sonora_warmfar(fb(64) + FLOOR(0.5), 60, 2),
    "wf60-floor50": sonora_warmfar(fb(64) + FLOOR(0.5), 60, 1),
})

def best_window(far, seconds, threshold=1e-5):
    """Start sample of the contiguous window of `seconds` with the most far-end activity (10 ms frames)."""
    n = len(far) // 160
    act = ((far[: n * 160].reshape(n, 160) ** 2).mean(axis=1) > threshold).astype(float)
    w = min(n, seconds * 100)
    c = np.concatenate([[0], np.cumsum(act)])
    k = int(np.argmax(c[w:] - c[:-w] if w < n else [c[-1]]))
    return k * 160, w * 160

def sonora_bestwin(tuning, seconds=60, repeats=1):
    def run(mic, far):
        shift = localvqe.prime(mic, far)
        ms = shift // 16 if shift >= 0 else None
        t = tuning + (";" + EXT if ms is not None else "")
        s, w = best_window(far, seconds)
        wm = np.tile(mic[s:s + w], repeats); wf = np.tile(far[s:s + w], repeats)
        out = sonora.run(np.concatenate([wm, mic]), np.concatenate([wf, far]), t, stream_delay_ms=ms, stats=False)[0]
        return out[len(wm):]
    return run

VARIANTS.update({
    "bw60-linear": sonora_bestwin(fb(64) + ";output=linear", 60),
    "bw90-linear": sonora_bestwin(fb(64) + ";output=linear", 90),
    "bw60-floor50": sonora_bestwin(fb(64) + FLOOR(0.5), 60),
    "bw90-floor50": sonora_bestwin(fb(64) + FLOOR(0.5), 90),
})
