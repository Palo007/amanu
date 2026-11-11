import sys, numpy as np
sys.path.insert(0, __import__("os").path.dirname(__import__("os").path.abspath(__file__)))
import lab, localvqe
W = 30 * 16000
for name in sys.argv[2].split(","):
    sc = lab.load_recording(name)
    whole = localvqe.prime(sc["mic"], sc["far"])
    wins = [localvqe.prime(sc["mic"][i:i+W], sc["far"][i:i+W]) for i in range(0, len(sc["mic"]) - W // 2, W)]
    ok = [s for s in wins if s >= 0]
    agree = sum(abs(s - whole) <= 128 for s in ok)
    print(f"{name:28s} whole {whole:5d} windows {wins} agree {agree}/{len(ok)}")
