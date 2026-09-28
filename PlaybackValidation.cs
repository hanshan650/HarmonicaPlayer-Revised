namespace HarmonicaPlayer;

// Extracted following PR #1's shared-validation/test approach. v0.2.0 rejected scores
// whose notes are too short to hold; v0.3.7 reports them instead, so an import that
// keeps the file's own tempo stays playable and only the affected notes may not sound.
public static class PlaybackValidation
{
    public const int PreparationMs = 12;
    // Roughly how long the game needs the key down before a note sounds.
    public const int MinimumHoldMs = 40;

    public static int ParseBpm(string text)
    {
        if (!int.TryParse(text, out int bpm) || bpm is < 20 or > 300)
            throw new FormatException("速度（BPM）范围20～300，请输入整数。");
        return bpm;
    }

    public static int ParseGap(string text)
    {
        if (!int.TryParse(text, out int silence) || silence is < 10 or > 5000)
            throw new FormatException("音符间隔／留白须为10～5000毫秒的整数。");
        return silence;
    }

    // 短音报告：Silence 是间隔毫秒数，TooShort 是按不住（扣除留白与准备后不足 MinimumHoldMs）
    // 的音数，FirstShortPosition 是第一个过短音在谱面里的位置（没有则为 -1），ShortestMs 是最短音时长。
    public sealed record GapCheck(int Silence, int TooShort, int FirstShortPosition, double ShortestMs);

    // 只校验“音符间隔”的取值；短音返回报告而不中断演奏（休止符不需要按住）。
    public static GapCheck CheckGap(string text, IReadOnlyCollection<ScoreNote> notes, double beatMs)
    {
        if (!double.IsFinite(beatMs) || beatMs <= 0) throw new FormatException("每拍时长无效。");
        int silence = ParseGap(text);
        int tooShort = 0, firstShort = -1;
        double shortest = double.PositiveInfinity;
        foreach (var note in notes)
        {
            if (note.Degree == 0) continue;
            double hold = note.Beats * beatMs;
            if (hold < shortest) shortest = hold;
            if (hold - silence - PreparationMs >= MinimumHoldMs) continue;
            tooShort++;
            if (firstShort < 0) firstShort = note.Position;
        }
        return new(silence, tooShort, firstShort, double.IsPositiveInfinity(shortest) ? 0 : shortest);
    }

    // 沿用旧名：只返回间隔毫秒数，短音不再抛异常（见 CheckGap）。
    public static int ValidateGap(string text, IReadOnlyCollection<ScoreNote> notes, double beatMs)
        => CheckGap(text, notes, beatMs).Silence;
}
