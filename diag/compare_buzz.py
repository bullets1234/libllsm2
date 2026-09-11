import wave, numpy as np, sys

def load(path):
    with wave.open(path, 'rb') as w:
        fs = w.getframerate()
        n = w.getnframes()
        data = w.readframes(n)
        x = np.frombuffer(data, dtype=np.int16).astype(np.float64) / 32768.0
        return fs, x

def hf_ratio_per_block(fs, x, block_s=5.0, hf_cutoff=8000.0):
    block = int(fs * block_s)
    nblocks = len(x) // block
    ratios = []
    for b in range(nblocks):
        seg = x[b*block:(b+1)*block]
        if not np.all(np.isfinite(seg)):
            seg = np.nan_to_num(seg)
        spec = np.abs(np.fft.rfft(seg * np.hanning(len(seg))))
        freqs = np.fft.rfftfreq(len(seg), 1.0/fs)
        total = np.sum(spec**2) + 1e-20
        hf = np.sum(spec[freqs > hf_cutoff]**2)
        rms = np.sqrt(np.mean(seg**2) + 1e-20)
        dbfs = 20*np.log10(rms + 1e-12)
        ratios.append((hf/total, dbfs))
    return ratios

for path in sys.argv[1:]:
    fs, x = load(path)
    ratios = hf_ratio_per_block(fs, x)
    print(f"=== {path} (fs={fs}, dur={len(x)/fs:.1f}s) ===")
    for i, (r, dbfs) in enumerate(ratios):
        print(f"  t={i*5:4d}-{i*5+5:4d}s  HF-ratio={r:.5f}  RMS={dbfs:6.1f} dBFS")
