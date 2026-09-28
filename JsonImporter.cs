using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace HarmonicaPlayer;

public sealed record JsonImportResult(string Title, int Bpm, string Body, string[] Warnings, int? LoweredFromBpm = null);

// 一条音轨的概览：导入前由“选择音轨”弹窗展示，也用于提示文案。
public sealed record JsonTrack(string Label, string Name, int Notes, int LowestPitch, int HighestPitch,
    int ChordNotes, double AverageDurationMs)
{
    public string Describe()
    {
        string chords = ChordNotes > 0 ? $"｜{ChordNotes}处同时发声" : "";
        return $"音轨{Label}｜{Name}｜{Notes}个音｜音域{LowestPitch}–{HighestPitch}｜平均{AverageDurationMs:0}ms{chords}";
    }
}

// Reads a score JSON (notes carry a MIDI pitch plus start and duration in seconds) and
// produces single-voice score text: any object with a notes array using
// pitch/start/duration is accepted.
// Multi-track files are arrangements (melody / accompaniment / separate players): only one
// track is played - the one the user picks, defaulting to the lowest-numbered track, which
// is normally the melody. The tempo is a label only: playback keeps the file's absolute
// times, so a note too short to hold is reported instead of slowing the whole piece down.
public static class JsonImporter
{
    public const int MaxBytes = 16 * 1024 * 1024;
    // 0.1ms: values produced from a musical grid survive this tolerance.
    private const double UnitTolerance = 1e-4;
    // Beat subdivisions a grid step may represent; all divide the 1/48-beat grid.
    private static readonly int[] BeatSteps = { 16, 12, 8, 24, 6, 4, 3, 2, 1, 48 };
    // Share of the values a finer grid step still has to explain before we accept it.
    private const double CoverageFloor = 0.95;
    // Tempo shown when the source has no single time base; a label only, never a rescale.
    private const int NeutralTempo = 120;

    public static JsonImportResult Read(string path, string? trackLabel = null)
    {
        if (new FileInfo(path).Length > MaxBytes) throw new IOException("JSON 文件不得超过16MB。");
        return Import(File.ReadAllText(path, Encoding.UTF8), Path.GetFileNameWithoutExtension(path), trackLabel);
    }

    // 供“导入 JSON”在导入前列出音轨：返回顺序就是默认优先顺序，下标 0 即默认音轨。
    public static IReadOnlyList<JsonTrack> ReadTracks(string path)
    {
        if (new FileInfo(path).Length > MaxBytes) throw new IOException("JSON 文件不得超过16MB。");
        return ListTracks(File.ReadAllText(path, Encoding.UTF8));
    }

    public static IReadOnlyList<JsonTrack> ListTracks(string text)
    {
        using var document = ParseDocument(text);
        var root = RequireRoot(document);
        return OrderTracks(root, Collect(root, out _));
    }

    public static JsonImportResult Import(string text, string? fileName, string? trackLabel = null)
    {
        using (var document = ParseDocument(text))
        {
            var root = RequireRoot(document);
            var warnings = new List<string>();
            // format 字段仅供参考：解析一律以 notes 里的 pitch/start/duration 为准。
            var buckets = Collect(root, out int skipped);
            if (skipped > 0) warnings.Add($"已忽略{skipped}条缺少字段、时值为零或音高非法的记录。");

            var ordered = OrderTracks(root, buckets);
            var chosen = trackLabel is { Length: > 0 }
                ? ordered.FirstOrDefault(t => t.Label == trackLabel) ?? ordered[0]
                : ordered[0];
            var raw = buckets[chosen.Label];
            if (ordered.Count > 1)
                warnings.Add($"检测到{ordered.Count}条音轨，已演奏{chosen.Describe()}；其余音轨不演奏，导入弹窗里可以更换。");

            int bpm;
            double scale; // JSON time units → beats
            if (Number(root, "bpm", out double declared) && declared is >= 20 and <= 300)
            {
                // An explicit tempo means start/duration are already beats.
                bpm = (int)Math.Round(declared);
                scale = 1.0;
                warnings.Add($"使用 JSON 中的速度{bpm} BPM，并据此把 start/duration 当作拍数处理。");
            }
            else
            {
                bpm = InferTempo(raw, warnings);
                scale = bpm / 60.0; // seconds → beats
            }

            var melody = ScoreText.Melody(
                raw.Select(n => new ScoreText.Note(n.Start * scale, n.End * scale, n.Pitch)), out int merged);
            if (melody.Count == 0) throw new FormatException("JSON 中没有可演奏的音符。");
            if (merged > 0) warnings.Add($"所选音轨内有{merged}处重叠，已合并为单音旋律（同时发声保留最高音）。");

            int shift = ScoreText.BestOctaveShift(melody);
            var events = ScoreText.Timeline(melody, shift, out int folded, out int truncated, out double shortest);
            if (shift != 0)
                warnings.Add($"整首已{(shift > 0 ? "升高" : "降低")}{Math.Abs(shift)}个八度以适配口琴音域（低音1～高音7，另含最高do）。");
            if (folded > 0)
                warnings.Add($"有{folded}个音超出音域（低音1～高音7，另含最高do），已按八度折回，局部音程会有变化。");
            if (truncated > 0) warnings.Add($"有{truncated}个音超过64拍上限，已截断为64拍。");
            // 保真优先：不为个别短音把整首速度降下来（那会让节奏整体变慢）。
            // 短音会按能按住的最长时间触发，这里只报告，不改动任何时间。
            double beatMs = 60000.0 / bpm;
            int tooShort = events.Count(e => e.Pitch >= 0 &&
                e.Beats * beatMs - ScoreText.DefaultGapMs - PlaybackValidation.PreparationMs < PlaybackValidation.MinimumHoldMs);
            if (tooShort > 0)
            {
                int floorMs = PlaybackValidation.PreparationMs + ScoreText.DefaultGapMs + PlaybackValidation.MinimumHoldMs;
                int suggestedGap = (int)Math.Floor(shortest * beatMs) - PlaybackValidation.PreparationMs - PlaybackValidation.MinimumHoldMs;
                string hint = suggestedGap is >= 10 and <= 5000
                    ? $"把“音符间隔”调到{suggestedGap}ms或更小，可以让它们发得出声。"
                    : ScoreText.SuggestedMaxBpm(shortest, bpm) is int slower
                        ? $"即使“音符间隔”调到最小也不够；要让每个音都发声，可把速度降到{slower} BPM（整首会变慢）。"
                        : "即使“音符间隔”调到最小也不够，建议合并或加长这些过短的音。";
                warnings.Add($"有{tooShort}个音短于可按住下限（{floorMs}ms），已保持原速度：它们会按能按住的最长时间触发，游戏里可能不发声。{hint}");
            }

            string title = Title(root, fileName);
            warnings.Insert(0, $"来源 JSON：{title}；已转换为单音简谱，不会修改原文件。保存后为 HarmonicaPlayer/1 TXT。");
            string body = ScoreText.Compose(events);
            if (ScoreParser.Parse(body).Count == 0) throw new FormatException("JSON 转换后没有可演奏的音符。");
            return new(title, bpm, body, warnings.ToArray());
        }
    }

    // 解析 JSON；允许注释和尾随逗号，与旧版行为一致。
    private static JsonDocument ParseDocument(string text)
    {
        try
        {
            return JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64,
            });
        }
        catch (JsonException e) { throw new FormatException("JSON 解析失败：" + e.Message); }
    }

    private static JsonElement RequireRoot(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("JSON 根节点必须是对象。");
        return root;
    }

    // 读取 notes 并按 track 分组。
    private static Dictionary<string, List<(double Start, double End, int Pitch)>> Collect(JsonElement root, out int skipped)
    {
        if (!root.TryGetProperty("notes", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new FormatException("JSON 缺少 notes 数组（每条需要 pitch、start、duration 三个数值字段）。");
        var buckets = new Dictionary<string, List<(double Start, double End, int Pitch)>>(StringComparer.Ordinal);
        int usable = 0;
        skipped = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !Number(item, "pitch", out double pitch) ||
                !Number(item, "start", out double start) || !Number(item, "duration", out double duration) ||
                !double.IsFinite(pitch) || !double.IsFinite(start) || !double.IsFinite(duration) ||
                duration <= 0 || (int)Math.Round(pitch) is < 0 or > 127)
            { skipped++; continue; }
            string label = item.TryGetProperty("track", out var track) ? TrackLabel(track) : "";
            if (!buckets.TryGetValue(label, out var bucket))
                buckets[label] = bucket = new List<(double Start, double End, int Pitch)>();
            bucket.Add((start, start + duration, (int)Math.Round(pitch)));
            usable++;
        }
        if (usable == 0) throw new FormatException("notes 数组里没有可用的音符（需要 pitch、start、duration 三个数值字段）。");
        return buckets;
    }

    // 默认优先顺序：编号最小的音轨在前（第一声部通常就是主旋律），
    // 编号相同或无编号时音数多的在前，弹窗里第一条就是默认选中的那一条。
    private static List<JsonTrack> OrderTracks(JsonElement root,
        Dictionary<string, List<(double Start, double End, int Pitch)>> buckets)
        => buckets.Select(p => Summarise(root, p.Key, p.Value))
            .OrderBy(t => TrackId(t.Label))
            .ThenByDescending(t => t.Notes)
            .ThenBy(t => t.Label, StringComparer.Ordinal)
            .ToList();

    // 数字编号按数值排序；非数字编号排在数字编号之后。
    private static int TrackId(string label)
        => label.Length > 0 && int.TryParse(label, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
            ? id : int.MaxValue;

    // 音轨概览：音数、音域、同时发声（和弦）数、平均音长，用于选择音轨与提示文案。
    private static JsonTrack Summarise(JsonElement root, string label, List<(double Start, double End, int Pitch)> notes)
    {
        var onsets = new Dictionary<long, int>();
        foreach (var note in notes)
        {
            long ms = (long)Math.Round(note.Start * 1000);
            onsets[ms] = onsets.TryGetValue(ms, out int count) ? count + 1 : 1;
        }
        return new(label, TrackName(root, label), notes.Count,
            notes.Min(n => n.Pitch), notes.Max(n => n.Pitch),
            onsets.Values.Sum(v => v - 1),
            notes.Average(n => (n.End - n.Start) * 1000));
    }

    // start/duration are seconds; recover the grid step they were written on, then
    // express it as 1/k of a beat so the tempo is a plain integer in range. The tempo only
    // labels the score - playback keeps the source's absolute times - so when the source
    // has no single time base we say so instead of inventing a tempo.
    private static int InferTempo(List<(double Start, double End, int Pitch)> notes, List<string> warnings)
    {
        var values = new List<double>();
        foreach (var note in notes)
        {
            if (note.Start > 0) values.Add(note.Start);
            values.Add(note.End - note.Start);
        }
        double unit = GreatestUnit(values);
        int best = 0;
        double bestScore = double.MaxValue;
        foreach (int steps in BeatSteps)
        {
            double raw = 60.0 / (unit * steps);
            int rounded = (int)Math.Round(raw);
            if (rounded is < 20 or > 300 || Math.Abs(raw - rounded) > 0.02) continue;
            // Prefer a normal tempo range, then the value closest to 120 BPM.
            double score = Math.Abs(rounded - 120.0) + (rounded is >= 60 and <= 180 ? 0 : 1000);
            if (score < bestScore) { bestScore = score; best = rounded; }
        }
        if (best > 0) return best;
        warnings.Add($"源文件的时间基准不统一，无法推断速度；已按{NeutralTempo} BPM 导入（只影响显示和手动调速），演奏仍按原文件的绝对时间对齐。");
        return NeutralTempo;
    }

    // Fuzzy greatest common divisor of the grid values. A finer divisor is accepted only
    // while it still explains almost every value; otherwise the source's own rounding
    // noise would keep splitting the grid until the step means nothing.
    private static double GreatestUnit(List<double> values)
    {
        double unit = values.Min();
        for (int pass = 0; pass < 8; pass++)
        {
            double candidate = double.MaxValue;
            foreach (double value in values)
            {
                double remainder = Math.Abs(value - Math.Round(value / unit) * unit);
                if (remainder > UnitTolerance && remainder < candidate) candidate = remainder;
            }
            if (candidate == double.MaxValue || candidate >= unit * 0.99) break;
            if (Coverage(values, candidate) < CoverageFloor) break;
            unit = candidate;
        }
        return unit;
    }

    // Share of the values that sit on the given step, allowing for the source's rounding.
    private static double Coverage(List<double> values, double unit)
    {
        double tolerance = Math.Max(UnitTolerance, unit * 0.02);
        int onGrid = values.Count(value => Math.Abs(value - Math.Round(value / unit) * unit) <= tolerance);
        return (double)onGrid / values.Count;
    }

    private static string Title(JsonElement root, string? fileName)
    {
        string candidate = ReadString(root, "title") ?? fileName ?? "";
        candidate = new string(candidate.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (candidate.Length > 200) candidate = candidate[..200];
        return candidate.Length == 0 ? "JSON 导入" : candidate;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Number(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Number) return property.TryGetDouble(out value);
        return property.ValueKind == JsonValueKind.String &&
            double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string TrackLabel(JsonElement track) => track.ValueKind switch
    {
        JsonValueKind.String => track.GetString() ?? "",
        JsonValueKind.Number => track.GetRawText(),
        _ => "",
    };

    // 曲谱可以在顶层 tracks 里给出音轨名：{ "1": "Violin" }。
    private static string TrackName(JsonElement root, string label)
    {
        if (label.Length > 0 && root.TryGetProperty("tracks", out var map) && map.ValueKind == JsonValueKind.Object &&
            map.TryGetProperty(label, out var name) && name.ValueKind == JsonValueKind.String &&
            name.GetString() is { Length: > 0 } text)
            return $"“{text}”";
        return label.Length > 0 ? $"轨道{label}" : "（无音轨标记）";
    }
}
