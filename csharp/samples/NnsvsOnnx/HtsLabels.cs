using System.Globalization;

namespace NnsvsOnnx;

/// <summary>
/// Minimal port of nnmnkwii.io.hts.HTSLabelFile (phone-alignment full-context
/// labels only; state-alignment labels are not needed for this model).
/// Times are in 100ns units (HTS convention).
/// </summary>
public sealed class HtsLabelFile
{
    public List<long> StartTimes { get; } = new();
    public List<long> EndTimes { get; } = new();
    public List<string> Contexts { get; } = new();

    /// <summary>Frame shift in 100ns units, used by <see cref="Round"/>.</summary>
    public long FrameShift { get; set; } = 50000;

    public int Count => StartTimes.Count;

    public static HtsLabelFile Load(string path) => LoadFromLines(File.ReadLines(path));

    /// <summary>Parses HTS full-context-label lines already in memory (e.g. produced
    /// by <see cref="UstToHtsConverter.Convert"/>), same format/semantics as <see cref="Load"/>.</summary>
    public static HtsLabelFile LoadFromLines(IEnumerable<string> rawLines)
    {
        var labels = new HtsLabelFile();
        bool isSecFormat = false;

        foreach (var rawLine in rawLines)
        {
            if (rawLine.Length == 0 || rawLine[0] == '#') continue;
            var cols = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length != 3)
            {
                // Phone-only label (1 column): not used by this project, skip defensively.
                if (cols.Length == 1) continue;
                throw new InvalidDataException($"Unsupported HTS label line: '{rawLine}'");
            }

            string startStr = cols[0], endStr = cols[1], context = cols[2];
            if (startStr.Contains('.') || endStr.Contains('.')) isSecFormat = true;

            long start, end;
            if (isSecFormat)
            {
                start = (long)(1e7 * double.Parse(startStr, CultureInfo.InvariantCulture));
                end = (long)(1e7 * double.Parse(endStr, CultureInfo.InvariantCulture));
            }
            else
            {
                start = long.Parse(startStr, CultureInfo.InvariantCulture);
                end = long.Parse(endStr, CultureInfo.InvariantCulture);
            }

            labels.StartTimes.Add(start);
            labels.EndTimes.Add(end);
            labels.Contexts.Add(context);
        }

        return labels;
    }

    /// <summary>Rounds start/end times to the nearest multiple of <see cref="FrameShift"/>
    /// (mirrors nnmnkwii's HTSLabelFile.round_).</summary>
    public void Round()
    {
        for (int i = 0; i < Count; i++)
        {
            StartTimes[i] = (long)Math.Round((double)StartTimes[i] / FrameShift, MidpointRounding.ToEven) * FrameShift;
            EndTimes[i] = (long)Math.Round((double)EndTimes[i] / FrameShift, MidpointRounding.ToEven) * FrameShift;
        }
    }

    public bool IsStateAlignmentLabel()
    {
        var c = Contexts[0];
        return c.Length >= 3 && c[^1] == ']' && c[^3] == '[';
    }

    public int NumPhones() => IsStateAlignmentLabel() ? throw new NotSupportedException("state alignment not supported") : Count;

    public long NumFrames(long frameShift) => EndTimes[^1] / frameShift;

    /// <summary>Returns a new label file containing only the given (0-based) row indices,
    /// preserving order (mirrors nnmnkwii's HTSLabelFile.__getitem__ with a list index).</summary>
    public HtsLabelFile Select(IReadOnlyList<int> indices)
    {
        var result = new HtsLabelFile { FrameShift = FrameShift };
        foreach (var idx in indices)
        {
            result.StartTimes.Add(StartTimes[idx]);
            result.EndTimes.Add(EndTimes[idx]);
            result.Contexts.Add(Contexts[idx]);
        }
        return result;
    }

    /// <summary>Mirrors nnsvs.io.hts.get_note_indices: indices where the start time
    /// differs from the previous row's start time (i.e. the first row of each
    /// contiguous "note" group).</summary>
    public List<int> GetNoteIndices()
    {
        var noteIndices = new List<int> { 0 };
        long lastStart = StartTimes[0];
        for (int idx = 1; idx < Count; idx++)
        {
            if (StartTimes[idx] != lastStart)
            {
                noteIndices.Add(idx);
                lastStart = StartTimes[idx];
            }
        }
        return noteIndices;
    }

    /// <summary>Mirrors nnsvs.io.hts._is_silence for full-context labels.</summary>
    private static bool IsSilenceContext(string context)
    {
        bool isFullContext = context.Contains('@');
        return isFullContext
            ? context.Contains("-sil") || context.Contains("-pau")
            : context == "sil" || context == "pau";
    }

    /// <summary>Mirrors nnsvs.io.hts.compute_nosil_duration (threshold=5.0 s):
    /// total duration in seconds of the given rows, excluding silences longer
    /// than the threshold.</summary>
    private double ComputeNosilDuration(List<int> rows, double threshold = 5.0)
    {
        double sum = 0;
        foreach (int i in rows)
        {
            double d = (EndTimes[i] - StartTimes[i]) * 1e-7;
            if (IsSilenceContext(Contexts[i]) && d > threshold) continue;
            sum += d;
        }
        return sum;
    }

    /// <summary>Faithful port of nnsvs.io.hts.segment_labels: splits the label file
    /// into phrase segments at sil/pau boundaries. Returns each segment (start times
    /// re-offset to zero, as in the Python version) together with its original
    /// (global) start time, so callers can recover the segment's absolute frame
    /// offset in the song timeline.</summary>
    public List<(HtsLabelFile Seg, long GlobalStartTime)> SegmentLabels(
        double silenceThreshold = 0.1, double minDuration = 5.0, double forceSplitThreshold = 5.0)
    {
        var startIndices = new List<int>();
        var endIndices = new List<int>();
        var seg = new List<int>(); // row indices of the segment under construction
        int si = 0;
        bool doneLastLabel = false;

        for (int idx = 0; idx < Count; idx++)
        {
            double d = (EndTimes[idx] - StartTimes[idx]) * 1e-7;
            bool isSilence = IsSilenceContext(Contexts[idx]);
            double segD = seg.Count > 0 ? ComputeNosilDuration(seg) : 0;

            if ((isSilence && d > forceSplitThreshold) ||
                (isSilence && d > silenceThreshold && segD > minDuration))
            {
                if (idx == Count - 1)
                {
                    // handled by the trailing "last label" logic below
                }
                else if (seg.Count > 0)
                {
                    startIndices.Add(si);
                    if (isSilence && d > forceSplitThreshold)
                    {
                        endIndices.Add(idx - 1);
                        startIndices.Add(idx);
                        endIndices.Add(idx);
                        seg = new List<int>();
                    }
                    else
                    {
                        seg.Add(idx);
                        endIndices.Add(idx);
                        seg = new List<int>();
                    }
                    si = idx + 1;
                }
                else
                {
                    seg.Add(idx);
                    startIndices.Add(si);
                    endIndices.Add(idx);
                    seg = new List<int>();
                }
            }
            else
            {
                if (seg.Count == 0) si = idx;
                if (idx == Count - 1) doneLastLabel = true;
                seg.Add(idx);
            }
        }

        if (seg.Count > 0)
        {
            double segD = ComputeNosilDuration(seg);
            // If the last segment is short, combine with the previous segment.
            if (segD < minDuration && endIndices.Count > 1)
            {
                endIndices[^1] = si + seg.Count - 1;
            }
            else
            {
                startIndices.Add(si);
                endIndices.Add(si + seg.Count - 1);
            }

            if (!doneLastLabel)
            {
                double d = (EndTimes[^1] - StartTimes[^1]) * 1e-7;
                if (IsSilenceContext(Contexts[^1]) && d > silenceThreshold)
                {
                    startIndices.Add(endIndices[^1]);
                    endIndices.Add(endIndices[^1]);
                }
            }
        }

        var segments = new List<(HtsLabelFile, long)>();
        for (int k = 0; k < startIndices.Count; k++)
        {
            var slice = new HtsLabelFile { FrameShift = FrameShift };
            long offset = StartTimes[startIndices[k]];
            for (int i = startIndices[k]; i <= endIndices[k]; i++)
            {
                slice.StartTimes.Add(StartTimes[i] - offset);
                slice.EndTimes.Add(EndTimes[i] - offset);
                slice.Contexts.Add(Contexts[i]);
            }
            segments.Add((slice, offset));
        }
        return segments;
    }
}
