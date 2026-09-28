using System.Globalization;
using System.Text;

namespace HarmonicaPlayer;

// Shared notation helpers for the MIDI and JSON importers. Both importers first
// produce notes measured in beats, then this class merges polyphony into a single
// melody, fits the harmonica range, quantises the rhythm and writes score text.
internal static class ScoreText
{
    // 960 steps per quarter note keep every 1/48 value, triplet and 1/16 exact, and hold
    // the quantisation grid near 1 ms at 60 BPM. JSON sources need that: their times are
    // plain seconds, not multiples of a musical beat.
    public const int Grid = 960;
    // Silences shorter than a 64th note are articulation, not rests.
    private const double RestThreshold = 1.0 / 16;
    // Range: （1）= C3 48 … 【7】= B5 83, plus 【【1】】= C6 84.
    public const int MinPitch = 48;
    public const int MaxPitch = 84;
    public const int DefaultGapMs = 20;

    public sealed record Note(double Start, double End, int Pitch);

    public static double Quantise(double beats) => Math.Round(beats * Grid, MidpointRounding.AwayFromZero) / Grid;

    // Merges polyphony into one line: notes sounding together keep the highest pitch, while
    // a note that starts later truncates the one it interrupts so the line is never lost.
    public static List<Note> Melody(IEnumerable<Note> notes, out int merged)
    {
        var ordered = notes.OrderBy(n => n.Start).ThenByDescending(n => n.Pitch).ToList();
        var result = new List<Note>();
        merged = 0;
        if (ordered.Count == 0) return result;
        Note current = ordered[0];
        for (int i = 1; i < ordered.Count; i++)
        {
            var note = ordered[i];
            if (note.Start >= current.End) { result.Add(current); current = note; continue; }
            merged++;
            if (note.Pitch == current.Pitch)
            {
                if (note.End > current.End) current = current with { End = note.End };
                continue;
            }
            // 同时发声（和弦）：保留最高的音；后起一个可听分量的音（连奏、或长音下方的走句）：
            // 把被打断的音截到它的起点并保留它，否则整条旋律线会被长音吞掉。
            bool interrupted = note.Start - current.Start >= RestThreshold;
            if (note.Pitch > current.Pitch || interrupted)
            {
                if (interrupted) result.Add(current with { End = note.Start });
                current = note;
            }
        }
        result.Add(current);
        return result;
    }

    public static bool InRange(int pitch) => pitch is >= MinPitch and <= MaxPitch;

    // Prefer the single whole-octave shift that keeps the most notes playable.
    public static int BestOctaveShift(IReadOnlyCollection<Note> notes)
    {
        int best = 0, bestCount = -1;
        for (int shift = -4; shift <= 4; shift++)
        {
            int inside = notes.Count(n => InRange(n.Pitch + 12 * shift));
            if (inside > bestCount || (inside == bestCount && Math.Abs(shift) < Math.Abs(best)))
            { best = shift; bestCount = inside; }
        }
        return best;
    }

    public static int FitPitch(int pitch, ref int folded)
    {
        int fitted = pitch;
        while (fitted > MaxPitch) fitted -= 12;
        while (fitted < MinPitch) fitted += 12;
        if (fitted != pitch) folded++;
        return fitted;
    }

    // A single rest event may not exceed the 64 beats the parser accepts.
    private static void AddRest(List<(double Beats, int Pitch)> events, double beats)
    {
        while (beats > 64) { events.Add((64, -1)); beats -= 64; }
        if (beats > 0) events.Add((beats, -1));
    }

    // Silences shorter than a 64th note join the previous event, so imported scores
    // are not filled with inaudible rests.
    private static void AddGap(List<(double Beats, int Pitch)> events, double beats)
    {
        if (events.Count > 0 && beats < RestThreshold)
        {
            var last = events[^1];
            double total = last.Beats + beats;
            if (total <= 64) { events[^1] = (total, last.Pitch); return; }
            events[^1] = (64, last.Pitch);
            beats = total - 64;
        }
        AddRest(events, beats);
    }

    // Turns a melody into (beats, pitch) events; pitch -1 marks a rest.
    public static List<(double Beats, int Pitch)> Timeline(IReadOnlyList<Note> melody, int shift,
        out int folded, out int truncated, out double shortest)
    {
        var events = new List<(double Beats, int Pitch)>();
        folded = 0;
        truncated = 0;
        double cursor = 0;
        foreach (var note in melody)
        {
            double start = Quantise(note.Start), end = Quantise(note.End);
            if (end <= cursor) continue;
            if (start < cursor) start = cursor;
            if (start > cursor) AddGap(events, start - cursor);
            int pitch = FitPitch(note.Pitch + 12 * shift, ref folded);
            double beats = end - start;
            if (beats > 64) { beats = 64; truncated++; }
            events.Add((beats, pitch));
            cursor = Math.Max(cursor, end);
        }
        shortest = events.Where(e => e.Pitch >= 0).Select(e => e.Beats).DefaultIfEmpty(1).Min();
        return events;
    }

    public static string Compose(List<(double Beats, int Pitch)> events)
    {
        var result = new StringBuilder();
        double lineBeats = 0;
        foreach (var (beats, pitch) in events)
        {
            // Break before an event that would overflow the four-beat line.
            if (lineBeats > 0 && lineBeats + beats > 4 + 1e-9)
            {
                result.Append(" |\n");
                lineBeats = 0;
            }
            if (result.Length > 0 && result[^1] != '\n') result.Append(' ');
            result.Append(Token(beats, pitch));
            lineBeats += beats;
            if (Math.Abs(lineBeats - 4) < 1e-9)
            {
                result.Append(" |\n");
                lineBeats = 0;
            }
        }
        return result.ToString().TrimEnd('\n');
    }

    // Null when every note satisfies the shared 12ms preparation + gap + 40ms hold rule.
    public static string? ShortNoteWarning(double shortestBeats, int bpm)
    {
        if (!NeedsSlowerTempo(shortestBeats, bpm)) return null;
        double holdMs = shortestBeats * 60000.0 / bpm;
        int? suggested = SuggestedMaxBpm(shortestBeats, bpm);
        return suggested is int slower
            ? $"最短音为{shortestBeats:0.####}拍（{bpm} BPM下约{holdMs:0.#}ms），扣除12ms准备和20ms间隔后不足40ms；请把速度降到{slower} BPM或更低，或把音符间隔调小。"
            : $"最短音仅{shortestBeats:0.####}拍，即使20 BPM也无法满足最短按住40ms；请在软件里合并这些过短的音或改用更长的时值。";
    }

    public static bool NeedsSlowerTempo(double shortestBeats, int bpm)
        => shortestBeats * 60000.0 / bpm - PlaybackValidation.PreparationMs - DefaultGapMs < PlaybackValidation.MinimumHoldMs;

    // Highest tempo the imported score still passes validation at; null when playable
    // already or when even 20 BPM would be too fast.
    public static int? SuggestedMaxBpm(double shortestBeats, int bpm)
    {
        if (!NeedsSlowerTempo(shortestBeats, bpm)) return null;
        int suggested = (int)Math.Floor(shortestBeats * 60000.0 /
            (PlaybackValidation.PreparationMs + DefaultGapMs + PlaybackValidation.MinimumHoldMs));
        return suggested >= 20 ? suggested : null;
    }

    private static string Token(double beats, int pitch)
        => (pitch < 0 ? "0" : PitchToken(pitch)) + DurationSuffix(beats);

    // Straight and dotted values use the short symbols; everything else uses the
    // explicit ":beats" form so no duration is rounded twice.
    private static string DurationSuffix(double beats)
    {
        if (beats == 1) return "";
        if (beats == 0.5) return "_";
        if (beats == 0.25) return "__";
        if (beats == 1.5) return ".";
        if (beats == 0.75) return "_.";
        if (beats == 0.375) return "__.";
        if (beats is >= 2 and <= 8 && Math.Abs(beats - Math.Round(beats)) < 1e-9)
            return " " + string.Join(" ", Enumerable.Repeat("—", (int)Math.Round(beats) - 1));
        return ":" + beats.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string PitchToken(int pitch)
    {
        int octave = pitch >= 72 ? 1 : pitch >= 60 ? 0 : -1;
        int pitchClass = ((pitch % 12) + 12) % 12;
        (int degree, bool sharp) = pitchClass switch
        {
            0 => (1, false), 1 => (1, true), 2 => (2, false), 3 => (2, true),
            4 => (3, false), 5 => (4, false), 6 => (4, true), 7 => (5, false),
            8 => (5, true), 9 => (6, false), 10 => (6, true), _ => (7, false),
        };
        if (pitch == MaxPitch) return "【【1】】"; // Highest do is written as a nested bracket.
        string note = (sharp ? "#" : "") + degree.ToString(CultureInfo.InvariantCulture);
        return octave switch { 1 => $"【{note}】", 0 => note, _ => $"（{note}）" };
    }
}
