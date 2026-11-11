"""Track N5 (overnight 2026-11-12): the training data of the tiny mask, fetched into its own folder.

    py -P n5_fetch.py speech [max compressed MB]   stream the FIRST part of FLEURS sk_sk train.tar.gz
    py -P n5_fetch.py noise                        DEMAND PSTATION and OHALLWAY (CC BY 4.0, zenodo 1227121)

Everything lands in %USERPROFILE%\\Amanu Quality Set\\lab\\overnight\\N5\\data. Speech is the FLEURS *train*
split only; a file is dropped when its sentence id, file name or normalized text occurs in the suite's
FLEURS test list or the held-out set's FLEURS dev list. The noises are DEMAND rooms that neither the suite
(OOFFICE, PCAFETER, STRAFFIC, DLIVING) nor the held-out set (OMEETING, PRESTO, DKITCHEN, SPSQUARE) uses.
The kept files are listed with SHA-256 in data/speech-files.tsv, the noises in data/noise-files.tsv.
"""
import hashlib, io, sys, tarfile, urllib.request, zipfile
sys.stdout.reconfigure(encoding="utf-8")
from pathlib import Path
import numpy as np
import soundfile as sf

ROOT = Path.home() / "Amanu Quality Set"
DATA = ROOT / "lab" / "overnight" / "N5" / "data"
UA = {"User-Agent": "amanu-quality-lab"}
HF = "https://huggingface.co/datasets/google/fleurs/resolve/main/data/sk_sk/"


def tsv(path):
    return [l.split("\t") for l in Path(path).read_text(encoding="utf-8").splitlines() if l.strip()]


def speech(max_mb=450):
    out = DATA / "speech"
    out.mkdir(parents=True, exist_ok=True)
    req = urllib.request.Request(HF + "train.tsv", headers=UA)
    train = {r[1]: r for r in tsv_from(urllib.request.urlopen(req, timeout=120).read().decode("utf-8"))}
    banned_ids, banned_files, banned_text = set(), set(), set()
    for p in (ROOT / "downloads" / "fleurs-sk-test.tsv", ROOT / "heldout" / "downloads" / "fleurs-sk-dev.tsv"):
        for r in tsv(p):
            banned_ids.add(r[0]); banned_files.add(r[1]); banned_text.add(r[3])
    print(f"train.tsv {len(train)} rows; excluded sets: {len(banned_ids)} ids, {len(banned_text)} texts", flush=True)
    req = urllib.request.Request(HF + "audio/train.tar.gz", headers=UA)
    resp = urllib.request.urlopen(req, timeout=120)
    counter = Counter(resp)
    kept, dropped = [], 0
    with tarfile.open(fileobj=counter, mode="r|gz") as tar:
        for m in tar:
            if not m.isfile() or not m.name.endswith(".wav"):
                continue
            base = m.name.split("/")[-1]
            row = train.get(base)
            if row is None or row[0] in banned_ids or base in banned_files or row[3] in banned_text:
                dropped += 1
                continue
            x, sr = sf.read(io.BytesIO(tar.extractfile(m).read()), dtype="int16")
            assert sr == 16000 and x.ndim == 1, (base, sr, x.shape)
            f = out / (base[:-4] + ".flac")
            sf.write(f, x, sr, format="FLAC", subtype="PCM_16")
            kept.append((base, row[0], row[6] if len(row) > 6 else "", len(x) / sr, hashlib.sha256(f.read_bytes()).hexdigest()))
            if len(kept) % 100 == 0:
                print(f"{len(kept)} kept, {dropped} dropped, {counter.n / 1e6:.0f} MB read", flush=True)
            if counter.n > max_mb * 1e6:
                break
    (DATA / "speech-files.tsv").write_text("file\tsentence_id\tgender\tseconds\tsha256_flac\n" + "".join(
        f"{a}\t{b}\t{c}\t{d:.2f}\t{e}\n" for a, b, c, d, e in kept), encoding="utf-8")
    print(f"done: {len(kept)} files, {sum(k[3] for k in kept) / 3600:.2f} h, {dropped} dropped, {counter.n / 1e6:.0f} MB read", flush=True)


def tsv_from(text):
    return [l.split("\t") for l in text.splitlines() if l.strip()]


class Counter(io.RawIOBase):
    """Reads a response and counts the compressed bytes, so the download can stop at a limit."""
    def __init__(self, resp):
        self.r, self.n = resp, 0
    def readable(self):
        return True
    def readinto(self, b):
        d = self.r.read(len(b))
        b[:len(d)] = d
        self.n += len(d)
        return len(d)


def noise():
    out = DATA / "noise"
    out.mkdir(parents=True, exist_ok=True)
    rows = []
    for name in ("PSTATION", "OHALLWAY"):
        req = urllib.request.Request(f"https://zenodo.org/records/1227121/files/{name}_16k.zip", headers=UA)
        raw = urllib.request.urlopen(req, timeout=300).read()
        print(name, len(raw) // 10 ** 6, "MB", flush=True)
        with zipfile.ZipFile(io.BytesIO(raw)) as z:
            chans = sorted(n for n in z.namelist() if n.endswith(".wav"))[::4]   # four of the sixteen channels, to save disk
            for n in chans:
                x, sr = sf.read(io.BytesIO(z.read(n)), dtype="int16")
                assert sr == 16000, sr
                x = x if x.ndim == 1 else x[:, 0]
                f = out / f"{name}-{Path(n).stem}.flac"
                sf.write(f, x, sr, format="FLAC", subtype="PCM_16")
                rows.append((f.name, n, len(x) / sr, hashlib.sha256(f.read_bytes()).hexdigest()))
    (DATA / "noise-files.tsv").write_text("file\tzip_member\tseconds\tsha256_flac\n" + "".join(
        f"{a}\t{b}\t{c:.1f}\t{d}\n" for a, b, c, d in rows), encoding="utf-8")
    print("noise files", len(rows))


if __name__ == "__main__":
    if sys.argv[1] == "speech":
        speech(float(sys.argv[2]) if len(sys.argv) > 2 else 450)
    else:
        noise()
