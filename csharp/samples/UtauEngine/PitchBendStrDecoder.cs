using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace enusampler
{
    internal class PitchBendStrDecoder()
    {

        public int FRAMERATE = 44100; // 44.1kHz
        public float pyworldPeriod = 5.0f;

        public List<int> Decode(string str)
        {
            // Decode the pitch bend string

            var strArray = str.Split("#", StringSplitOptions.RemoveEmptyEntries);
            List<int> res = new List<int>();
            for (int i = 0; i < strArray.Length; i += 2)
            {
                //var p = strArray[i..(i + 2)];

                int rangeSize = 2;

                int start = i;
                int end = Math.Min(i + rangeSize, strArray.Length); // 範囲を配列の長さで制限
                string[] p = strArray[i..end];

                

                if (p.Length == 2)
                {
                    //Console.WriteLine($"Pitch Bend: {p[0]} Value: {p[1]}");
                    res.AddRange(toIntStream(p[0]));

                    for(int j = 0; j > Convert.ToInt32(p[1]); j++)
                    {
                        res.Add(res.Last());
                    }
                    
                }
                else
                {
                    //Console.WriteLine($"{p[0]}");
                    res.AddRange(toIntStream(p[0]));
                }

            }

            // 配列内のすべての値が最初の値と同じかどうかを確認
            if (res.All(x => x == res[0]))
            {
                // 同じなら、すべて0の配列を返す
                return new List<int>(new int[res.Count]);
            }
            else
            {
                // 違うなら、元のリストに0を追加して返す
                List<int> result = new List<int>(res);
                result.Add(0);
                return result;
            }

        }

        public int toUint(char x,char y)
        {
            // Decode the pitch bend char to uint


            int ans1 = 0, ans2 = 0, ans = 0;

            if (x == '+') ans1 = 62;
            if (x == '/') ans1 = 63;
            if (x >= '0' && x <= '9') ans1 = x + 4;
            if (x >= 'A' && x <= 'Z') ans1 = x - 65;
            if (x >= 'a' && x <= 'z') ans1 = x - 71;

            if (y == '+') ans2 = 62;
            if (y == '/') ans2 = 63;
            if (y >= '0' && y <= '9') ans2 = y + 4;
            if (y >= 'A' && y <= 'Z') ans2 = y - 65;
            if (y >= 'a' && y <= 'z') ans2 = y - 71;

            ans = (ans1 << 6) | ans2;
            if (ans >= 2048) ans -= 4096;
            return ans;

        }

        //public int toInt12(string b64Str) {
        //    var uint12 = toUint(b64Str[0]) << 6 | toUint(b64Str[1]);
        //    if (uint12 >= 2048)
        //    {
        //        uint12 -= 4096;
        //    }

        //    return (int)uint12;
        //}

        public List<int> toIntStream(string b64Str)
        {
            // Decode the pitch bend string to int stream
            int rangeSize = 2;

            List<int> intList = new List<int>();
            for (int i = 0; i < b64Str.Length; i+=2)
            {

                int start = i;
                int end = Math.Min(i + rangeSize, b64Str.Length); // 範囲を配列の長さで制限
                string p = b64Str[i..end];
                intList.Add(toUint(p[0], p[1]));

            }

            return intList;

        }


        public float[] getPitchRange(string _tempo , float targetMs , int frameRate = 44100)
        {
            // Decode the pitch range from the tempo string
            // 1. tempoを取得
            // 2. targetMsを取得
            // 3. frameRateを取得  
            // 4. tempoからピッチ範囲を計算

            //int.TryParse(_tempo, out int bpm);
            var bpm = 120;
            try
            {
                //Console.WriteLine(_tempo);
                //Console.WriteLine($"targetMS:{targetMs}");
                bpm = int.Parse(_tempo);
            }
            catch (Exception ex)
            {
                //Console.WriteLine(ex);
                if (_tempo[0..2] == "0Q")
                {
                    //Console.WriteLine("substring 2");
                    bpm = int.Parse(_tempo[2..]);
                }
                else
                {
                    //Console.WriteLine("substring 1");
                    bpm = int.Parse(_tempo[1..]);
                }
            
            }


            int nFrames = (int)(targetMs / 1000.0f * frameRate);
            int frameStep = (int)Math.Round(60.0 / 96.0 / bpm * frameRate);
            int pitchLength = nFrames / frameStep +1;
            float msStep = frameStep / (float)frameRate * 1000.0f;
            //Console.WriteLine($"Pitch Range: {pitchRange}");
            return Enumerable.Range(0, pitchLength + 1)
                         .Select(i => i * msStep)
                         .ToArray();
        }

        //public float[] InterpPitch(float[] basePitch, float[] utauT, float[] worldT)
        //{
        //    /*
        //    UTAUピッチ列をWorld時間軸に線形補完します。
        //    */

        //    // UTAUタイミング列がUTAUピッチ列よりも長い場合、0埋め
        //    if (utauT.Length > basePitch.Length)
        //    {
        //        int oldLength = basePitch.Length;
        //        Array.Resize(ref basePitch, utauT.Length);
        //        for (int i = oldLength; i < basePitch.Length; i++)
        //        {
        //            basePitch[i] = 0.0f; // 明示的に 0 を埋める
        //        }
        //        Console.WriteLine($"utauTLength{utauT.Length}");
        //        Console.WriteLine($"baseptichLength{basePitch.Length}");
        //    }
        //    // UTAUタイミング列がUTAUピッチ列よりも短い場合、切り詰め
        //    else
        //    {
        //        basePitch = basePitch.Take(utauT.Length).ToArray();
        //    }

        //    // 線形補間を実行
        //    var interpolator = Interp1d(utauT, worldT, basePitch);
        //    return interpolator;
        //}

        public float[] InterpPitch(float[] basePitch, float[] utauT, float[] worldT)
        {
            float[] paddedBase;
            if (utauT.Length > basePitch.Length)
            {
                // 0埋め
                paddedBase = new float[utauT.Length];
                Array.Copy(basePitch, paddedBase, basePitch.Length);
                // 残りは0のまま
            }
            else
            {
                paddedBase = new float[utauT.Length];
                Array.Copy(basePitch, paddedBase, utauT.Length);
            }

            foreach (var item in paddedBase)
            {
                Console.WriteLine($"{item}");
            }

            return Interp1d(utauT, worldT, paddedBase);
        }


        //static float[] Interp1d(float[] t, float[] newT, float[] value)
        //{
        //    /*
        //    tに従属するvalueを、newT間隔で線形補完した配列を返します。
        //    */

        //    float[] newValue = new float[newT.Length];
        //    float span = t[1] - t[0]; // 等間隔の幅

        //    for (int i = 0; i < newT.Length; i++)
        //    {
        //        int index = (int)(newT[i] / span);

        //        // 境界チェック
        //        if (index >= t.Length - 1) index = t.Length - 2;

        //        if (Math.Abs(t[index] - newT[i]) < 1e-9)
        //        {
        //            // 完全に一致する場合
        //            newValue[i] = value[index];
        //        }
        //        else
        //        {
        //            // 線形補間
        //            newValue[i] = (value[index] * (t[index + 1] - newT[i]) +
        //                           value[index + 1] * (newT[i] - t[index])) / span;
        //        }
        //    }

        //    return newValue;
        //}

        //public float[] Interp1d(float[] t, float[] newT, float[] value)
        //{
        //    /*
        //    t に従属する value を、newT 間隔で線形補完した配列を返します。
        //    */

        //    if (t.Length < 2 || newT.Length == 0 || value.Length < 2)
        //    {
        //        throw new ArgumentException("入力配列の長さが不正です。");
        //    }

        //    float[] newValue = new float[newT.Length];
        //    float span = t[1] - t[0];

        //    for (int i = 0; i < newT.Length; i++)
        //    {
        //        int index = (int)(newT[i] / span);

        //        if (index < 0 || index >= t.Length - 1)
        //        {
        //            throw new IndexOutOfRangeException("newT の値が t の範囲外です。");
        //        }

        //        if (Math.Abs(t[index] - newT[i]) < 1e-9) // 浮動小数点の誤差を考慮
        //        {
        //            newValue[i] = value[index];
        //        }
        //        else
        //        {
        //            float t1 = t[index];
        //            float t2 = t[index + 1];
        //            float v1 = value[index];
        //            float v2 = value[index + 1];

        //            newValue[i] = (v1 * (t2 - newT[i]) + v2 * (newT[i] - t1)) / (t2 - t1);
        //        }
        //    }

        //    return newValue;
        //}


        public static float[] Interp1d(float[] t, float[] newT, float[] value)
        {
            float[] newValue = new float[newT.Length];
            float span = t[1] - t[0];
            for (int i = 0; i < newT.Length; i++)
            {
                // newT[i]がtのどの区間にあるかを計算
                int index = (int)((newT[i] - t[0]) / span);
                if (index < 0) index = 0;
                if (index >= t.Length - 1) index = t.Length - 2; // 範囲外防止

                if (Math.Abs(t[index] - newT[i]) < 1e-6)
                {
                    newValue[i] = value[index];
                }
                else
                {
                    newValue[i] = (value[index] * (t[index + 1] - newT[i]) + value[index + 1] * (newT[i] - t[index])) / span;
                }
            }
            return newValue;
        }


        public float GetNoteTime(float tempo, int noteLengthTicks)
        {
            /*
            UTAUのノート長（ticks）をテンポ（BPM）に基づいて秒に変換します。
            */
            if (tempo <= 0)
            {
                throw new ArgumentException("テンポは0より大きい値を指定してください。");
            }

            // UTAU基準では1拍（4分音符）が480 ticks
            const float ticksPerBeat = 480.0f;

            return (noteLengthTicks * 60.0f) / (tempo * ticksPerBeat);
        }

        public int CalculateFrameCount(float totalTime, float pyworldPeriod)
        {
            /*
            合計時間とPYWORLD_PERIODからフレーム数を計算します。
            */
            if (pyworldPeriod <= 0)
            {
                throw new ArgumentException("PYWORLD_PERIOD は 0 より大きい値を指定してください。");
            }

            // ms を秒に変換
            float periodInSeconds = pyworldPeriod / 1000.0f;

            // フレーム数を計算し、切り上げ
            return (int)Math.Ceiling(totalTime / periodInSeconds);
        }

        public float[] GenerateTimePositions(float pyworldPeriod, int frameCount)
        {
            /*
            各フレームの時間位置（秒）を計算します。
            */

            float[] t = new float[frameCount];
            float periodInSeconds = pyworldPeriod / 1000.0f; // ms -> 秒

            for (int i = 0; i < frameCount; i++)
            {
                t[i] = i * periodInSeconds;
            }

            return t;
        }
    }

    struct Int12
    {
        private short _value;

        public Int12(short value)
        {
            if (value < -2048 || value > 2047)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "値が12ビット整数の範囲外です。");
            }
            _value = value;
        }

        public short Value
        {
            get => (short)(_value & 0xFFF); // 12ビット制約
            set
            {
                if (value < -2048 || value > 2047)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "値が12ビット整数の範囲外です。");
                }
                _value = value;
            }
        }

        public override string ToString() => _value.ToString();
    }
}
