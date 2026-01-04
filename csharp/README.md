# LlsmBindings (C# SafeHandle wrappers)

This library provides high-level, SafeHandle-based C# bindings for:
- `libllsm2.dll` (LLSM analyze/synthesize + frame parameter editing)
- `libpyin.dll` (F0 estimation)

## Build

1) Ensure native binaries exist (x64 Release):
- `g:\libllsm2\x64\Release\libllsm2.dll`
- `g:\libllsm2\x64\Release\libpyin.dll`

2) Build the C# library:
```powershell
cd g:\libllsm2\csharp
dotnet build -c Release
```

## Usage sketch

```csharp
using LlsmBindings;

// load WAV as float[] x, sample rate fs
float[] x = ...; float fs = 48000f;

// 1) F0 estimation
float[] f0 = Pyin.Analyze(x, fs, nhop: 128, fmin: 50f, fmax: 500f);

// 2) Analysis
using var aopt = Llsm.CreateAnalysisOptions();
var conf = Llsm.AOptionsToConf(aopt, fs/2f);
int nfrm = (int)MathF.Round((x.Length / fs) / Llsm.GetThopSeconds(conf));
using var chunk = Llsm.Analyze(aopt, x, fs, f0, nfrm);

// 3) Edit parameters (example: double the first frame's Rd)
var frame0 = Llsm.GetFrame(chunk, 0);
Llsm.SetFrameRd(frame0, 2.0f);

// 4) Synthesis
using var sopt = Llsm.CreateSynthesisOptions(fs);
using var output = Llsm.Synthesize(sopt, chunk);
float[] y = Llsm.ReadOutput(output);
```

Place `libllsm2.dll` and `libpyin.dll` next to your app executable (or add their folder to `PATH`). Ensure your .NET app runs as x64.

## Samples

- `samples/SmokeTest` : `temp.wav` を読み込み、ピッチシフト + タイムストレッチを適用して出力します。
	- 使い方: フォルダに `temp.wav` (16-bit PCM mono) を置く
	- 引数: `<semitones> <stretch>` (stretch > 1 で遅く / < 1 で速く)
	- 例: +4 半音 & 1.25 倍時間伸長
		```powershell
		cd g:\libllsm2\csharp\samples\SmokeTest
		dotnet run -c Release -- 4 1.25
		```
	- 出力: `out_ps+4_ts1_25.wav`
