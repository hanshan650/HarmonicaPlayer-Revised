using System.Globalization;
using System.IO;
using System.Text;

namespace HarmonicaPlayer;

public sealed record MidiImportResult(string Title, int Bpm, string Body, string[] Warnings, int? LoweredFromBpm = null);

// 一条含音符的轨道概览：导入前由“选择音轨”弹窗展示，也用于提示文案。
public sealed record MidiTrack(int Index, string Name, int Notes, int Percussion,
    int LowestPitch, int HighestPitch, int ChordNotes, double AverageDurationMs)
{
    public string Describe()
    {
        string chords = ChordNotes > 0 ? $"｜{ChordNotes}处同时发声" : "";
        string percussion = Percussion > 0 ? $"｜另忽略{Percussion}个打击乐" : "";
        return $"音轨{Index + 1}｜{Name}｜{Notes}个音｜音域{LowestPitch}–{HighestPitch}｜平均{AverageDurationMs:0}ms{chords}{percussion}";
    }
}

// Reads a Standard MIDI File (Format 0/1, metrical timing) and turns it into
// HarmonicaPlayer note text. Only a single melody line can be produced:
// percussion (channel 10) is ignored, overlapping notes keep the highest pitch,
// and pitches outside the harmonica range are folded by whole octaves. One track is
// played: the user picks it when the file has several, defaulting to the most notes.
public static class MidiImporter
{
    public const int MaxBytes = 16 * 1024 * 1024;

    private sealed record RawNote(long Start, long End, int Pitch);
    private sealed record RawTrack(string Name, int? Tempo, int TempoChanges, int Percussion, List<RawNote> Notes);

    public static MidiImportResult Read(string path, int? trackIndex = null)
        => Import(ReadAllBytes(path), Path.GetFileNameWithoutExtension(path), trackIndex);

    // 供“导入 MIDI”列出含音符的轨道：返回顺序就是默认优先顺序，下标 0 即默认音轨。
    public static IReadOnlyList<MidiTrack> ReadTracks(string path) => ListTracks(ReadAllBytes(path));

    public static IReadOnlyList<MidiTrack> ListTracks(byte[] data)
        => OrderTracks(Parse(data)).Select(x => x.Info).ToList();

    private static byte[] ReadAllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = stream.Read(chunk, 0, chunk.Length)) != 0)
        {
            if (buffer.Length + count > MaxBytes) throw new IOException("MIDI 文件不得超过16MB。");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    public static MidiImportResult Import(byte[] data, string? fileName, int? trackIndex = null)
    {
        var parsed = Parse(data);
        int division = parsed.Division;

        var warnings = new List<string>();
        var ordered = OrderTracks(parsed);
        if (ordered.Count == 0) throw new FormatException("MIDI 中没有可演奏的音符（可能只有打击乐或空轨道）。");
        var entry = ordered.FirstOrDefault(x => trackIndex is int index && x.Index == index);
        if (entry.Track is null) entry = ordered[0];
        var chosen = entry.Track;
        if (ordered.Count > 1)
            warnings.Add($"检测到{ordered.Count}条含音符的轨道，已演奏{entry.Info.Describe()}；其余轨道不演奏，导入弹窗里可以更换。");
        if (chosen.Percussion > 0)
            warnings.Add($"已忽略通道10的{chosen.Percussion}个打击乐音符。");

        int microseconds = parsed.Microseconds;
        int tempoEvents = parsed.TempoEvents;
        int bpm = (int)Math.Round(60000000.0 / microseconds, MidpointRounding.AwayFromZero);
        if (bpm is < 20 or > 300)
        {
            int clamped = Math.Clamp(bpm, 20, 300);
            warnings.Add($"MIDI速度为{bpm} BPM，超出软件允许的20～300，已按{clamped} BPM导入。");
            bpm = clamped;
        }
        if (tempoEvents > 1)
            warnings.Add($"MIDI含{tempoEvents}个速度事件，本版只使用第一个速度（{bpm} BPM）；变速段落会按固定速度演奏。");

        var melody = ScoreText.Melody(
            chosen.Notes.Select(n => new ScoreText.Note(n.Start / (double)division, n.End / (double)division, n.Pitch)),
            out int merged);
        if (melody.Count == 0) throw new FormatException("MIDI 中没有可演奏的音符。");
        if (merged > 0) warnings.Add($"存在{merged}处重叠，已合并为单音旋律（同时发声保留最高音，后起的音截断被它打断的长音）。");

        int shift = ScoreText.BestOctaveShift(melody);
        var events = ScoreText.Timeline(melody, shift, out int folded, out int truncated, out double shortest);
        if (shift != 0)
            warnings.Add($"整首已{(shift > 0 ? "升高" : "降低")}{Math.Abs(shift)}个八度以适配口琴音域（低音1～高音7，另含最高do）。");
        if (folded > 0)
            warnings.Add($"有{folded}个音超出音域（低音1～高音7，另含最高do），已按八度折回，局部音程会有变化。");
        if (truncated > 0) warnings.Add($"有{truncated}个音超过64拍上限，已截断为64拍。");
        // Notes are measured in beats, so switching tempo only slows the piece down.
        int? loweredFrom = null;
        if (ScoreText.SuggestedMaxBpm(shortest, bpm) is int slower)
        {
            loweredFrom = bpm;
            bpm = slower;
            warnings.Add($"为保证能按住每个音，速度已从 {loweredFrom} BPM 自动调整为 {bpm} BPM（音高与时值不变，播放放慢）。");
        }
        else if (ScoreText.ShortNoteWarning(shortest, bpm) is string hold) warnings.Add(hold);

        string title = Title(chosen.Name, fileName);
        warnings.Insert(0, $"来源 MIDI：{title}；已转换为单音简谱，不会修改原文件。保存后为 HarmonicaPlayer/1 TXT。");
        string body = ScoreText.Compose(events); // Never hand the UI a score it cannot play.
        if (ScoreParser.Parse(body).Count == 0) throw new FormatException("MIDI 转换后没有可演奏的音符。");
        return new(title, bpm, body, warnings.ToArray(), loweredFrom);
    }

    private sealed record ParsedMidi(int Division, int Microseconds, int TempoEvents, List<RawTrack> Tracks);

    // 校验文件头并读出所有 MTrk 轨道；格式问题在这里一次性报错。
    private static ParsedMidi Parse(byte[] data)
    {
        if (data.Length < 14 || Encoding.ASCII.GetString(data, 0, 4) != "MThd")
            throw new FormatException("不是标准 MIDI 文件（缺少 MThd 头）。");
        int headerSize = ReadInt32(data, 4);
        if (headerSize < 6 || data.Length < 8 + headerSize)
            throw new FormatException("MIDI 文件头不完整。");
        int format = ReadInt16(data, 8);
        int trackCount = ReadInt16(data, 10);
        int division = ReadInt16(data, 12);
        if (format is not (0 or 1))
            throw new FormatException($"暂不支持 MIDI Format {format}，请另存为 Format 0 或 1。");
        if (trackCount <= 0) throw new FormatException("MIDI 文件没有轨道。");
        if (division <= 0 || division > 0x7FFF) throw new FormatException("暂不支持 SMPTE 时间格式的 MIDI（division 为负值）。");

        var tracks = new List<RawTrack>();
        int offset = 8 + headerSize;
        for (int i = 0; i < trackCount && offset + 8 <= data.Length; i++)
        {
            string id = Encoding.ASCII.GetString(data, offset, 4);
            int length = ReadInt32(data, offset + 4);
            offset += 8;
            if (length < 0 || offset + length > data.Length) throw new FormatException("MIDI 轨道数据不完整。");
            if (id == "MTrk") tracks.Add(ReadTrack(data, offset, length));
            offset += length;
        }
        int microseconds = tracks.FirstOrDefault(t => t.Tempo != null)?.Tempo ?? 500000;
        return new(division, microseconds, tracks.Sum(t => t.TempoChanges), tracks);
    }

    // 默认优先顺序：音符最多的轨道在前（v0.3.4 起的行为），下标 0 即默认音轨。
    private static List<(int Index, RawTrack Track, MidiTrack Info)> OrderTracks(ParsedMidi parsed)
    {
        double beatMs = parsed.Microseconds / 1000.0;
        return parsed.Tracks
            .Select((track, index) => (Index: index, Track: track))
            .Where(x => x.Track.Notes.Count > 0)
            .Select(x => (x.Index, x.Track, Info: Summarise(x.Track, x.Index, parsed.Division, beatMs)))
            .OrderByDescending(x => x.Info.Notes)
            .ThenBy(x => x.Index)
            .ToList();
    }

    // 轨道概览：音数、音域、同时发声（和弦）、平均音长、需要忽略的打击乐数。
    private static MidiTrack Summarise(RawTrack track, int index, int division, double beatMs)
    {
        var onsets = new Dictionary<long, int>();
        foreach (var note in track.Notes)
            onsets[note.Start] = onsets.TryGetValue(note.Start, out int count) ? count + 1 : 1;
        return new(index, string.IsNullOrWhiteSpace(track.Name) ? "未命名" : $"“{track.Name}”",
            track.Notes.Count, track.Percussion,
            track.Notes.Min(n => n.Pitch), track.Notes.Max(n => n.Pitch),
            onsets.Values.Sum(v => v - 1),
            track.Notes.Average(n => (n.End - n.Start) / (double)division * beatMs));
    }

    private static string Title(string? trackName, string? fileName)
    {
        string candidate = string.IsNullOrWhiteSpace(trackName) ? fileName ?? "" : trackName!;
        candidate = new string(candidate.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (candidate.Length > 200) candidate = candidate[..200];
        return candidate.Length == 0 ? "MIDI 导入" : candidate;
    }

    private static RawTrack ReadTrack(byte[] data, int start, int length)
    {
        int position = start, end = start + length;
        long tick = 0;
        int running = 0, percussion = 0, tempoChanges = 0;
        int? tempo = null;
        string name = "";
        var notes = new List<RawNote>();
        var active = new Dictionary<(int Channel, int Pitch), long>();

        int Variable()
        {
            int value = 0, b;
            do
            {
                if (position >= end) throw new FormatException("MIDI 事件数据不完整。");
                b = data[position++];
                value = (value << 7) | (b & 0x7F);
                if (value > 0x0FFFFFFF) throw new FormatException("MIDI 变长数值超出范围。");
            } while ((b & 0x80) != 0);
            return value;
        }

        while (position < end)
        {
            tick += Variable();
            if (position >= end) break;
            int status = data[position];
            if (status >= 0x80) { position++; running = status < 0xF0 ? status : 0; }
            else status = running;
            if (status < 0x80) throw new FormatException("MIDI 轨道缺少状态字节。");

            if (status == 0xFF)
            {
                if (position >= end) throw new FormatException("MIDI 元事件不完整。");
                int type = data[position++];
                int size = Variable();
                if (position + size > end) throw new FormatException("MIDI 元事件超出轨道长度。");
                if (type == 0x2F) break;
                if (type == 0x03 && name.Length == 0) name = Encoding.UTF8.GetString(data, position, size).Trim();
                else if (type == 0x51 && size == 3)
                {
                    int microseconds = (data[position] << 16) | (data[position + 1] << 8) | data[position + 2];
                    if (microseconds > 0) { tempo ??= microseconds; tempoChanges++; }
                }
                position += size;
                continue;
            }
            if (status is 0xF0 or 0xF7)
            {
                int size = Variable();
                if (position + size > end) throw new FormatException("MIDI 系统专用事件超出轨道长度。");
                position += size;
                continue;
            }

            int high = status & 0xF0, channel = status & 0x0F, data1, data2 = 0;
            if (high is 0xC0 or 0xD0)
            {
                if (position >= end) throw new FormatException("MIDI 事件数据不完整。");
                data1 = data[position++];
            }
            else
            {
                if (position + 2 > end) throw new FormatException("MIDI 事件数据不完整。");
                data1 = data[position++];
                data2 = data[position++];
            }

            if (high is 0x80 or 0x90)
            {
                bool on = high == 0x90 && data2 > 0;
                if (channel == 9)
                {
                    if (on) percussion++;
                    continue;
                }
                if (data1 > 127) continue;
                if (on) active[(channel, data1)] = tick;
                else if (active.Remove((channel, data1), out long began) && tick > began)
                    notes.Add(new(began, tick, data1));
            }
        }
        return new(name, tempo, tempoChanges, percussion, notes);
    }

    private static int ReadInt16(byte[] data, int offset) => (data[offset] << 8) | data[offset + 1];

    private static int ReadInt32(byte[] data, int offset)
        => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}
