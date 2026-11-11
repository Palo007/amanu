import sys, time, numpy as np
sys.path.insert(0, __import__("os").path.dirname(__import__("os").path.abspath(__file__)))
import lab, localvqe, ctypes as C
d = localvqe.lib(); PF = C.POINTER(C.c_float)
for sc in [lab.load_recording(n) for n in ("echo-office-ser-5-snrclean","echo-office-ser+5-snr-5","echo-cafe-ser-5-snrclean","echo-cafe-ser+5-snr-5","echo-street-ser-5-snr+0","aec-real")]:
    ctx = d.localvqe_new(str(localvqe.HERE / localvqe.MODELS["fast"]).encode())
    m = np.ascontiguousarray(sc["mic"], np.float32); f = np.ascontiguousarray(sc["far"], np.float32)
    t0 = time.time(); d.localvqe_prime_delay_begin(ctx); n = len(m) - len(m) % 128
    d.localvqe_prime_delay_feed(ctx, m.ctypes.data_as(PF), f.ctypes.data_as(PF), n); s = d.localvqe_prime_delay_end(ctx)
    print(sc["name"], "prime shift", s, f"{time.time()-t0:.2f}s for {len(m)/16000:.0f}s audio")
    d.localvqe_free(ctx)
