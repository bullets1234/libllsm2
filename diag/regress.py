# -*- coding: utf-8 -*-
"""L2R エンジン回帰テスト。

合成母音を生成し、代表的なレンダリングパターンを一括実行して
  1. 終了コード / 出力 WAV の妥当性
  2. 決定論性（同一コマンド 2 回で同一バイト列）
  3. ピッチダウン時の高域倍音連続性（旧 vsphse バグの再発検知: fnyq/2 に崖がないか）
を検証する。

使い方:
  python diag/regress.py [L2R.exe へのパス]
  （省略時は csharp/samples/UtauEngine_NewGen/publish/L2R.exe）
"""
import hashlib
import math
import os
import random
import struct
import subprocess
import sys
import tempfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_EXE = os.path.join(
    REPO, "csharp", "samples", "UtauEngine_NewGen", "publish", "L2R.exe")

FS = 44100
SRC_F0 = 220.0


def make_test_wav(path):
    """100ms ノイズバースト + 900ms の倍音リッチな 220Hz 母音（ビブラート付き）。"""
    n = FS
    random.seed(42)
    phase = [0.0] * 60
    burst = int(0.1 * FS)

    def har_amp(k):
        f = k * SRC_F0
        a = 1.0 / k
        for fc, bw, g in ((700, 130, 3.0), (1200, 150, 2.0), (2600, 250, 1.5)):
            a *= 1.0 + g * math.exp(-((f - fc) ** 2) / (2 * bw * bw))
        return a

    samples = []
    for i in range(n):
        t = i / FS
        if i < burst:
            s = (random.random() * 2 - 1) * 0.3 * math.sin(math.pi * i / burst)
        else:
            cur = SRC_F0 * (1.0 + 0.005 * math.sin(2 * math.pi * 5.5 * t))
            s = 0.0
            for k in range(1, 60):
                fk = k * cur
                if fk > FS / 2 * 0.95:
                    break
                phase[k] += 2 * math.pi * fk / FS
                s += har_amp(k) * math.sin(phase[k])
            s *= 0.12
            s += (random.random() * 2 - 1) * 0.005
            rel = (i - burst) / (n - burst)
            if rel < 0.02:
                s *= rel / 0.02
            if rel > 0.95:
                s *= (1 - rel) / 0.05
        samples.append(max(-1.0, min(1.0, s)))

    data = b"".join(struct.pack("<h", int(v * 32767)) for v in samples)
    hdr = struct.pack("<4sI4s4sIHHIIHH4sI", b"RIFF", 36 + len(data), b"WAVE",
                      b"fmt ", 16, 1, 1, FS, FS * 2, 2, 16, b"data", len(data))
    with open(path, "wb") as f:
        f.write(hdr + data)


def read_wav(path):
    with open(path, "rb") as f:
        b = f.read()
    fs = struct.unpack_from("<I", b, 24)[0]
    pos = 12
    while pos < len(b) - 8:
        cid, sz = b[pos:pos + 4], struct.unpack_from("<I", b, pos + 4)[0]
        if cid == b"data":
            d = b[pos + 8:pos + 8 + sz]
            return fs, [struct.unpack_from("<h", d, i * 2)[0] / 32768.0
                        for i in range(len(d) // 2)]
        pos += 8 + sz + (sz % 2)
    raise ValueError("no data chunk: " + path)


def goertzel_db(x, fs, freq):
    wc = 2 * math.pi * freq / fs
    cw = 2 * math.cos(wc)
    s1 = s2 = 0.0
    for v in x:
        s0 = v + cw * s1 - s2
        s2, s1 = s1, s0
    p = math.sqrt(max(1e-30, s1 * s1 + s2 * s2 - cw * s1 * s2))
    return 20 * math.log10(p / (len(x) / 4) + 1e-12)


def harmonic_level(path, f0, k):
    fs, s = read_wav(path)
    N = 16384
    off = max(0, len(s) // 2 - N // 2)
    w = [0.5 - 0.5 * math.cos(2 * math.pi * i / (N - 1)) for i in range(N)]
    x = [s[off + i] * w[i] for i in range(N)]
    fk = k * f0
    return max(goertzel_db(x, fs, fq) for fq in (fk - 3, fk - 1.5, fk, fk + 1.5, fk + 3))


def md5(path):
    with open(path, "rb") as f:
        return hashlib.md5(f.read()).hexdigest()


def run(exe, args, env_extra=None):
    env = dict(os.environ, L2R_LOG="Warn")
    if env_extra:
        env.update(env_extra)
    r = subprocess.run([exe] + args, env=env, capture_output=True, timeout=120)
    return r.returncode


def main():
    exe = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_EXE
    if not os.path.exists(exe):
        print(f"NG: L2R.exe not found: {exe}")
        return 1

    tmp = tempfile.mkdtemp(prefix="l2r_regress_")
    src = os.path.join(tmp, "src.wav")
    make_test_wav(src)
    print(f"exe: {exe}\ntmp: {tmp}\n")

    failures = 0

    def check(name, ok, detail=""):
        nonlocal failures
        print(f"{'OK ' if ok else 'NG '} {name}" + (f"  ({detail})" if detail else ""))
        if not ok:
            failures += 1

    # 1. 代表パターンのレンダリング（終了コード + 出力妥当性）
    cases = [
        ("pitch-down A2",       ["A2", "100", "",            "0", "800", "100", "0", "100", "0"]),
        ("pitch-up A4 + flags", ["A4", "130", "C50g-8B70T5", "0", "600", "100", "0", "100", "50"]),
        ("same-pitch A3",       ["A3", "100", "",            "0", "800", "100", "0", "100", "0"]),
        ("U12 flag",            ["A3", "100", "U12",         "0", "600", "100", "0", "100", "0"]),
        ("L flag",              ["A3", "100", "L",           "0", "600", "100", "0", "100", "0"]),
        ("G50 growl",           ["A3", "100", "G50",         "0", "600", "100", "0", "100", "0"]),
        ("decimal tempo + pb",  ["A3", "100", "",            "0", "600", "100", "0", "100", "0",
                                 "!120.5", "AAABACADAEAFAGAH#20#BABBBCBD"]),
        # 引数は same-pitch A3 とフラグ以外同一にする（Y/Q 効果の md5 比較用）
        ("Y60 hybrid exc",      ["A3", "100", "Y60",         "0", "800", "100", "0", "100", "0"]),
        ("Q60 voice quality",   ["A3", "100", "Q60",         "0", "800", "100", "0", "100", "0"]),
    ]
    outs = {}
    for i, (name, args) in enumerate(cases):
        out = os.path.join(tmp, f"case{i}.wav")
        code = run(exe, [src, out] + args)
        ok = code == 0 and os.path.exists(out) and os.path.getsize(out) > 1000
        if ok:
            _, s = read_wav(out)
            peak = max(abs(v) for v in s)
            ok = 0.001 < peak <= 1.0 and all(math.isfinite(v) for v in s[:100])
        outs[name] = out
        check(f"render: {name}", ok, f"exit={code}")

    # 2. 決定論性（B60 と G50 それぞれ 2 回レンダリングで同一ハッシュ）
    for name, flags in (("plain B60", "B60"), ("growl G50", "G50")):
        a = os.path.join(tmp, f"det_{flags}_a.wav")
        b = os.path.join(tmp, f"det_{flags}_b.wav")
        run(exe, [src, a, "A3", "100", flags, "0", "600", "100", "0", "100", "30"])
        run(exe, [src, b, "A3", "100", flags, "0", "600", "100", "0", "100", "30"])
        same = os.path.exists(a) and os.path.exists(b) and md5(a) == md5(b)
        check(f"determinism: {name}", same, md5(a)[:12] if same else "hash mismatch")

    # 3. ピッチダウン高域連続性: A2(110Hz) 出力で旧バグ境界 11025Hz の直下/直上の
    #    倍音レベル差が 25dB 未満であること（旧バグでは上側がノイズフロアへ消滅）
    down = outs.get("pitch-down A2")
    if down and os.path.exists(down):
        below = sum(harmonic_level(down, 110.0, k) for k in (96, 97, 98)) / 3
        above = sum(harmonic_level(down, 110.0, k) for k in (102, 103, 104)) / 3
        check("pitch-down: no 11kHz harmonic cliff", (below - above) < 25,
              f"below={below:.1f}dB above={above:.1f}dB diff={below-above:.1f}dB")

    # 3.5 レベル一貫性: 同一ソースを A2/A3/A4 でレンダリングしたとき、
    #     実効 RMS（無音除外）が基準レベル -16dBFS ±2dB に揃うこと
    def active_rms_db(path):
        fs, s = read_wav(path)
        fl = 441
        ms = []
        for f in range(0, len(s), fl):
            seg = s[f:f + fl]
            ms.append(sum(v * v for v in seg) / max(1, len(seg)))
        mx = max(ms)
        if mx <= 0:
            return -120.0
        act = [m for m in ms if m >= mx * 0.01]
        return 10 * math.log10(sum(act) / len(act) + 1e-20)

    TARGET_DB = -16.0
    levels = {}
    for pitch in ("A2", "A3", "A4"):
        out = os.path.join(tmp, f"lvl_{pitch}.wav")
        run(exe, [src, out, pitch, "100", "", "0", "700", "100", "0", "100", "0"])
        levels[pitch] = active_rms_db(out)
    off = max(abs(v - TARGET_DB) for v in levels.values())
    check("level normalization to -16dBFS", off < 2.0,
          " ".join(f"{p}={v:.1f}dB" for p, v in levels.items()))

    # 3.6 囁き保護: -40dB 以下の極小ソースが基準レベルまで爆音化しないこと
    #     （正規化ゲインは +12dB 止まり）
    quiet = os.path.join(tmp, "quiet_src.wav")
    fs_s, s_s = read_wav(src)
    data = b"".join(struct.pack("<h", int(v * 0.02 * 32767)) for v in s_s)  # -34dB
    hdr = struct.pack("<4sI4s4sIHHIIHH4sI", b"RIFF", 36 + len(data), b"WAVE",
                      b"fmt ", 16, 1, 1, fs_s, fs_s * 2, 2, 16, b"data", len(data))
    with open(quiet, "wb") as f:
        f.write(hdr + data)
    qout = os.path.join(tmp, "quiet_out.wav")
    run(exe, [quiet, qout, "A3", "100", "", "0", "700", "100", "0", "100", "0"])
    q_db = active_rms_db(qout)
    check("whisper protection (no blast to target)", q_db < TARGET_DB - 6.0,
          f"quiet source rendered at {q_db:.1f}dB (target={TARGET_DB:.0f}dB)")

    # 4. マイクロプロソディ: J80 で既定（オフ）と出力が変わり、
    #    かつ J 有効でも決定論的であること
    j0a = os.path.join(tmp, "j_off.wav")
    jda = os.path.join(tmp, "j80a.wav")
    jdb = os.path.join(tmp, "j80b.wav")
    run(exe, [src, j0a, "A3", "100", "", "0", "800", "100", "0", "100", "0"])
    run(exe, [src, jda, "A3", "100", "J80", "0", "800", "100", "0", "100", "0"])
    run(exe, [src, jdb, "A3", "100", "J80", "0", "800", "100", "0", "100", "0"])
    check("micro-prosody: J flag has audible effect",
          os.path.exists(j0a) and os.path.exists(jda) and md5(j0a) != md5(jda))
    check("micro-prosody: deterministic with J enabled", md5(jda) == md5(jdb))

    # 5. ハイブリッド励振: Y60 で出力が変わること
    ydef = outs.get("same-pitch A3")
    yout = outs.get("Y60 hybrid exc")
    check("hybrid excitation: Y flag has effect",
          ydef and yout and os.path.exists(yout) and md5(ydef) != md5(yout))

    # 6. 動的声質カーブ: Q60 で出力が変わること
    qout = outs.get("Q60 voice quality")
    check("voice quality curve: Q flag has effect",
          ydef and qout and os.path.exists(qout) and md5(ydef) != md5(qout))

    total = len(cases) + 9
    print(f"\n{'PASS' if failures == 0 else 'FAIL'}: {total-failures} ok, {failures} failed")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
