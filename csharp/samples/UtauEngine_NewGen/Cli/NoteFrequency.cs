using System;

namespace UtauEngineNg.Cli
{
    /// <summary>音名（"C4", "A#5", "Bb3"）↔ 周波数(Hz) の変換。A4=440Hz 平均律。</summary>
    public static class NoteFrequency
    {
        // A, B, C, D, E, F, G の半音オフセット
        private static readonly int[] NoteOffsets = { 9, 11, 0, 2, 4, 5, 7 };

        public static float NoteNameToHz(string? noteName)
        {
            if (string.IsNullOrEmpty(noteName)) return 440f;

            int noteIndex = 0;
            int octave = 4;
            int i = 0;

            char note = char.ToUpperInvariant(noteName[i++]);
            if (note >= 'A' && note <= 'G')
                noteIndex = NoteOffsets[note - 'A'];

            if (i < noteName.Length)
            {
                if (noteName[i] == '#') { noteIndex++; i++; }
                else if (noteName[i] == 'b') { noteIndex--; i++; }
            }

            if (i < noteName.Length && int.TryParse(noteName.Substring(i), out var oct))
                octave = oct;

            int midiNote = 12 * (octave + 1) + noteIndex;
            return 440f * (float)Math.Pow(2, (midiNote - 69) / 12.0);
        }
    }
}
