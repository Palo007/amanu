"""ctypes harness for sonora_ffi.dll, the way SonoraBackend drives it: 16 kHz mono,
10 ms frames, echo canceller only, render frame then capture frame."""
from __future__ import annotations

import ctypes as C
import os
from pathlib import Path

import numpy as np

RATE = 16000
FRAME = 160


class WapConfig(C.Structure):
    _fields_ = [
        ("pipeline_maximum_internal_processing_rate", C.c_int32),
        ("pipeline_multi_channel_render", C.c_bool),
        ("pipeline_multi_channel_capture", C.c_bool),
        ("pipeline_capture_downmix_method", C.c_int32),
        ("pre_amplifier_enabled", C.c_bool),
        ("pre_amplifier_fixed_gain_factor", C.c_float),
        ("capture_level_adjustment_enabled", C.c_bool),
        ("capture_level_adjustment_pre_gain_factor", C.c_float),
        ("capture_level_adjustment_post_gain_factor", C.c_float),
        ("analog_mic_gain_emulation_enabled", C.c_bool),
        ("analog_mic_gain_emulation_initial_level", C.c_int32),
        ("high_pass_filter_enabled", C.c_bool),
        ("high_pass_filter_apply_in_full_band", C.c_bool),
        ("echo_canceller_enabled", C.c_bool),
        ("echo_canceller_enforce_high_pass_filtering", C.c_bool),
        ("noise_suppression_enabled", C.c_bool),
        ("noise_suppression_level", C.c_int32),
        ("noise_suppression_analyze_linear_aec_output_when_available", C.c_bool),
        ("gain_controller2_enabled", C.c_bool),
        ("gain_controller2_fixed_digital_gain_db", C.c_float),
        ("gain_controller2_adaptive_digital_enabled", C.c_bool),
        ("gain_controller2_adaptive_digital_headroom_db", C.c_float),
        ("gain_controller2_adaptive_digital_max_gain_db", C.c_float),
        ("gain_controller2_adaptive_digital_initial_gain_db", C.c_float),
        ("gain_controller2_adaptive_digital_max_gain_change_db_per_second", C.c_float),
        ("gain_controller2_adaptive_digital_max_output_noise_level_dbfs", C.c_float),
        ("gain_controller2_input_volume_controller_enabled", C.c_bool),
    ]


class WapStreamConfig(C.Structure):
    _fields_ = [("sample_rate_hz", C.c_int32), ("num_channels", C.c_int32)]


class WapStats(C.Structure):
    _fields_ = [
        ("has_erl", C.c_bool), ("erl", C.c_double),
        ("has_erle", C.c_bool), ("erle", C.c_double),
        ("has_div", C.c_bool), ("div", C.c_double),
        ("has_dmed", C.c_bool), ("dmed", C.c_int32),
        ("has_dstd", C.c_bool), ("dstd", C.c_int32),
        ("has_rel", C.c_bool), ("rel", C.c_double),
        ("has_relmax", C.c_bool), ("relmax", C.c_double),
        ("has_delay", C.c_bool), ("delay", C.c_int32),
    ]


class WapAec3Debug(C.Structure):
    _fields_ = [("delay_changes", C.c_uint64), ("render_delay_blocks", C.c_int32), ("filter_delay_blocks", C.c_int32),
                ("usable_linear_estimate", C.c_bool), ("transparent_mode", C.c_bool), ("erle_db", C.c_float)]


DEFAULT_DLL = Path(os.environ.get("SONORA_DLL", Path(__file__).resolve().parents[3] / "native" / "sonora" / "sonora_ffi.dll"))
_libs: dict[str, C.CDLL] = {}


def lib(path: Path = DEFAULT_DLL) -> C.CDLL:
    key = str(path)
    if key not in _libs:
        d = C.CDLL(key)
        d.wap_config_default.restype = WapConfig
        d.wap_create_with_config.restype = C.c_void_p
        d.wap_create_with_config.argtypes = [WapConfig]
        d.wap_create_with_aec3_tuning.restype = C.c_void_p
        d.wap_create_with_aec3_tuning.argtypes = [WapConfig, C.c_char_p]
        d.wap_check_aec3_tuning.restype = C.c_int32
        d.wap_check_aec3_tuning.argtypes = [C.c_char_p]
        d.wap_initialize.argtypes = [C.c_void_p] + [WapStreamConfig] * 4
        d.wap_destroy.argtypes = [C.c_void_p]
        fp = C.POINTER(C.POINTER(C.c_float))
        for name in ("wap_process_stream_f32", "wap_process_reverse_stream_f32"):
            f = getattr(d, name)
            f.argtypes = [C.c_void_p, fp, WapStreamConfig, WapStreamConfig, fp]
            f.restype = C.c_int32
        d.wap_get_statistics.argtypes = [C.c_void_p, C.POINTER(WapStats)]
        d.wap_set_stream_delay_ms.argtypes = [C.c_void_p, C.c_int32]
        if hasattr(d, "wap_aec3_debug"):
            d.wap_aec3_debug.argtypes = [C.c_void_p, C.POINTER(WapAec3Debug)]
        _libs[key] = d
    return _libs[key]


def run(mic: np.ndarray, far: np.ndarray, tuning: str | None = None, stream_delay_ms: int | None = None,
        dll: Path = DEFAULT_DLL, stats: bool = True):
    """Returns the cleaned mic (same length, frame-aligned with the input) and per-frame stats arrays."""
    d = lib(dll)
    cfg = d.wap_config_default()
    cfg.echo_canceller_enabled = True
    cfg.echo_canceller_enforce_high_pass_filtering = False
    cfg.high_pass_filter_enabled = False
    cfg.noise_suppression_enabled = False
    cfg.gain_controller2_enabled = False
    cfg.capture_level_adjustment_enabled = False
    cfg.pre_amplifier_enabled = False
    cfg.analog_mic_gain_emulation_enabled = False
    if tuning is None:
        apm = d.wap_create_with_config(cfg)
    else:
        t = tuning.encode("ascii")
        bad = d.wap_check_aec3_tuning(t)
        if bad:
            raise ValueError(f"entry {bad} of {tuning!r} not understood")
        apm = d.wap_create_with_aec3_tuning(cfg, t)
    if not apm:
        raise RuntimeError("create failed")
    sc = WapStreamConfig(RATE, 1)
    if d.wap_initialize(apm, sc, sc, sc, sc) != 0:
        raise RuntimeError("init failed")
    n = len(mic) // FRAME
    out = np.zeros(len(mic), dtype=np.float32)
    m = np.ascontiguousarray(mic[: n * FRAME], dtype=np.float32)
    f = np.ascontiguousarray(far[: n * FRAME], dtype=np.float32)
    fo = np.zeros(FRAME, dtype=np.float32)
    mo = np.zeros(FRAME, dtype=np.float32)
    PF = C.POINTER(C.c_float)
    st = WapStats()
    erle = np.full(n, np.nan)
    erl = np.full(n, np.nan)
    delay = np.full(n, -1, dtype=np.int32)
    dbg = WapAec3Debug()
    has_dbg = stats and hasattr(d, "wap_aec3_debug")
    changes = np.zeros(n, dtype=np.int64); fdelay = np.zeros(n, dtype=np.int32)
    usable = np.zeros(n, dtype=bool); transparent = np.zeros(n, dtype=bool)
    fo_p = fo.ctypes.data_as(PF)
    mo_p = mo.ctypes.data_as(PF)
    for k in range(n):
        fin = f[k * FRAME:(k + 1) * FRAME]
        min_ = m[k * FRAME:(k + 1) * FRAME]
        a = PF(fin.ctypes.data_as(PF).contents)
        src = (PF * 1)(fin.ctypes.data_as(PF))
        dst = (PF * 1)(fo_p)
        if d.wap_process_reverse_stream_f32(apm, src, sc, sc, dst) != 0:
            raise RuntimeError("reverse failed")
        if stream_delay_ms is not None:
            d.wap_set_stream_delay_ms(apm, stream_delay_ms)
        src = (PF * 1)(min_.ctypes.data_as(PF))
        dst = (PF * 1)(mo_p)
        if d.wap_process_stream_f32(apm, src, sc, sc, dst) != 0:
            raise RuntimeError("capture failed")
        out[k * FRAME:(k + 1) * FRAME] = mo
        if stats:
            d.wap_get_statistics(apm, C.byref(st))
            if st.has_erle:
                erle[k] = st.erle
            if st.has_erl:
                erl[k] = st.erl
            if st.has_delay:
                delay[k] = st.delay
            if has_dbg and d.wap_aec3_debug(apm, C.byref(dbg)) == 0:
                changes[k] = dbg.delay_changes; fdelay[k] = dbg.filter_delay_blocks
                usable[k] = dbg.usable_linear_estimate; transparent[k] = dbg.transparent_mode
    d.wap_destroy(apm)
    return out.astype(np.float64), {"erle": erle, "erl": erl, "delay": delay, "changes": changes,
                                    "filter_delay": fdelay, "usable": usable, "transparent": transparent}
