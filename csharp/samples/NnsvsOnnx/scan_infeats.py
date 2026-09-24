import numpy as np, glob, os

d = r'g:\libllsm2\csharp\samples\NnsvsOnnx\smoke_out\casecaseinfeat'
files = sorted(glob.glob(d + r'\*_infeats_seg*.csv'),
               key=lambda p: int(p.split('seg')[-1].split('.')[0]))
lens = [sum(1 for _ in open(f)) for f in files]
print('segments:', len(files), 'total', sum(lens))

cum = 0
target = 16800
tf = None
off = 0
for f, L in zip(files, lens):
    if cum <= target < cum + L:
        print('target in', os.path.basename(f), 'offset', target - cum, 'len', L)
        tf = f
        off = target - cum
        break
    cum += L

X = np.loadtxt(tf, delimiter=',')
a = max(0, off - 100)
b = min(len(X), off + 300)
W = X[a:b]
res = []
for c in range(W.shape[1]):
    x = W[:, c]
    if x.std() < 1e-6:
        continue
    tr = np.convolve(x, np.ones(51) / 51, 'same')
    dd = (x - tr)[25:-25]
    if dd.std() < 1e-9:
        continue
    sp = np.abs(np.fft.rfft(dd * np.hanning(len(dd))))
    fr = np.fft.rfftfreq(len(dd), 1 / 200.)
    m = (fr > 3) & (fr < 9)
    frac = sp[m].sum() / max(sp[1:].sum(), 1e-12)
    res.append((c, dd.std(), frac))

res.sort(key=lambda z: -z[1] * z[2])
print('col  ddstd  frac3-9Hz')
for c, s, fr_ in res[:15]:
    print(f'{c:4d} {s:8.4f} {fr_:.3f}')
