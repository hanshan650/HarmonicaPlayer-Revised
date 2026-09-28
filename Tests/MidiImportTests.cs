using HarmonicaPlayer;
using System.Text;

// Independent checks for the MIDI importer: bytes are built by hand as a real
// Standard MIDI File instead of reusing MidiExporter helpers.
static class MidiImportTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("MIDI 导入: " + name); passed++; }
        static bool Throws(Action action)
        {
            try { action(); } catch (FormatException) { return true; } catch (IOException) { return true; }
            return false;
        }
        static bool Playable(string body, int bpm)
        {
            try { PlaybackValidation.ValidateGap("20", ScoreParser.Parse(body), 60000.0 / bpm); return true; }
            catch (FormatException) { return false; }
        }

        // Single track: title, tempo, straight rhythm, rest, sharp, highest do, chords, percussion.
        var basic = new Track { Name = "测试曲", Tempo = 500000 };
        basic.Add(0, 480, 60);      // 1
        basic.Add(480, 240, 62);    // 2_
        basic.Add(960, 720, 72);    // 【1】
        basic.Add(1680, 480, 84);   // 【【1】】
        basic.Add(2160, 480, 61);   // #1
        basic.Add(2640, 480, 67);   // 5, overlapped by 7 below
        basic.Add(2640, 480, 71);   // 7 wins
        basic.Notes.Add((2700, 300, 36, 9)); // percussion (channel 10), ignored
        var single = MidiImporter.Import(Build(0, 480, basic), "fallback");
        Check(single.Title == "测试曲" && single.Bpm == 120, "标题与速度来自文件内容");
        Check(single.Warnings.Any(w => w.Contains("打击乐")), "提示已忽略打击乐");
        Check(single.Warnings.Any(w => w.Contains("最高音")), "提示重叠音合并");
        var singleNotes = ScoreParser.Parse(single.Body);
        Check(singleNotes.Select(n => ScoreTimeline.Pitch(n)).SequenceEqual(new[] { 60, 62, -1, 72, 84, 61, 71 }),
            "音高、休止、重叠取最高音");
        Check(singleNotes.Select(n => n.Beats).SequenceEqual(new[] { 1.0, 0.5, 0.5, 1.5, 1.0, 1.0, 1.0 }),
            "时值与空隙转休止");
        Check(single.Body.Contains("【【1】】") && single.Body.Contains("【1】") && single.Body.Contains("#1") &&
            single.Body.Contains("0_"), "简谱写法");
        Check(single.Body.Contains(" |"), "每四拍换行加小节线");

        // Format 1: pick the track with the most notes; tempo may live on another track.
        var conductor = new Track { Name = "伴奏", Tempo = 400000 };
        conductor.Add(0, 480, 48);
        var melody = new Track { Name = "旋律" };
        melody.Add(0, 480, 60); melody.Add(480, 480, 62); melody.Add(960, 480, 64);
        var multi = MidiImporter.Import(Build(1, 480, conductor, melody), null);
        Check(multi.Title == "旋律" && multi.Bpm == 150, "多轨选择与速度");
        Check(multi.Warnings.Any(w => w.Contains("轨道")), "提示轨道选择");
        // 界面可以列出轨道（默认优先顺序：音符最多在前）并按编号指定。
        var listed = MidiImporter.ListTracks(Build(1, 480, conductor, melody));
        Check(listed.Count == 2 && listed[0].Name == "“旋律”" && listed[0].Notes == 3, "MIDI 音轨列表默认优先");
        Check(listed[1].Name == "“伴奏”" && listed[1].Notes == 1, "列出其余 MIDI 轨道");
        Check(listed[0].Describe().Contains("音轨") && listed[0].Describe().Contains("个音"), "MIDI 音轨说明文字");
        Check(MidiImporter.Import(Build(1, 480, conductor, melody), null, 0).Title == "伴奏", "按编号指定 MIDI 轨道");
        var unNamed = new Track();
        unNamed.Add(0, 480, 60);
        Check(MidiImporter.Import(Build(0, 480, unNamed), "文件名").Title == "文件名", "无轨道名时使用文件名");

        // Range handling: whole-octave shift, then folding for what cannot fit.
        var low = new Track();
        low.Add(0, 480, 36); low.Add(480, 480, 40);
        var shifted = MidiImporter.Import(Build(0, 480, low), "low");
        Check(ScoreParser.Parse(shifted.Body).Select(n => ScoreTimeline.Pitch(n)).SequenceEqual(new[] { 48, 52 }),
            "低音整体升八度");
        Check(shifted.Warnings.Any(w => w.Contains("八度")), "提示整体移调");
        var spread = new Track();
        spread.Add(0, 480, 48); spread.Add(480, 480, 96);
        var folded = MidiImporter.Import(Build(0, 480, spread), "spread");
        Check(ScoreParser.Parse(folded.Body).Select(n => ScoreTimeline.Pitch(n)).SequenceEqual(new[] { 48, 84 }),
            "超范围音折回八度");
        Check(folded.Warnings.Any(w => w.Contains("折回")), "提示折回");

        // Running status and note-on velocity 0 as note off.
        var running = new Track { RunningStatus = true };
        running.Add(0, 480, 60); running.Add(480, 480, 62);
        Check(ScoreParser.Parse(MidiImporter.Import(Build(0, 480, running), "rs").Body)
            .Select(n => ScoreTimeline.Pitch(n)).SequenceEqual(new[] { 60, 62 }), "支持跑动状态");
        var zeroOff = new Track { ZeroVelocityOff = true };
        zeroOff.Add(0, 480, 60); zeroOff.Add(480, 480, 62);
        Check(ScoreParser.Parse(MidiImporter.Import(Build(0, 480, zeroOff), "zv").Body)
            .Select(n => ScoreTimeline.Pitch(n)).SequenceEqual(new[] { 60, 62 }), "NoteOn力度0视为结束");

        // Triplets keep their value; over-long rests are split.
        var triplet = new Track();
        triplet.Add(0, 160, 60); triplet.Add(160, 160, 62); triplet.Add(320, 160, 64);
        var tri = MidiImporter.Import(Build(0, 480, triplet), "tri");
        Check(tri.Body.Contains(":0.3333") && Math.Abs(ScoreParser.Parse(tri.Body).Sum(n => n.Beats) - 1) < 1e-3,
            "三连音量化");
        var longRest = new Track();
        longRest.Add(0, 480, 60); longRest.Add(480 + 200 * 480, 480, 62);
        var rest = MidiImporter.Import(Build(0, 480, longRest), "rest");
        Check(Math.Abs(ScoreParser.Parse(rest.Body).Sum(n => n.Beats) - 202) < 1e-6, "超长休止拆分");
        var smallGap = new Track();
        smallGap.Add(0, 480, 60); smallGap.Add(500, 480, 62); smallGap.Add(1100, 480, 64);
        var gaps = ScoreParser.Parse(MidiImporter.Import(Build(0, 480, smallGap), "gap").Body);
        Check(gaps.Select(n => Math.Round(n.Beats, 4)).SequenceEqual(new[] { 1.0417, 1.0, 0.25, 1.0 }),
            $"极短空隙并入前音、真实休止保留 [{string.Join(",", gaps.Select(n => Math.Round(n.Beats, 4)))}]");

        // Very short notes warn about the shared minimum-hold rule.
        var quick = new Track { Tempo = 250000 };
        quick.Add(0, 60, 60); quick.Add(60, 480, 62);
        var fast = MidiImporter.Import(Build(0, 480, quick), "quick");
        Check(fast.Bpm == 104 && fast.LoweredFromBpm == 240, $"自动降到可演奏速度（{fast.LoweredFromBpm}→{fast.Bpm}）");
        Check(fast.Warnings.Any(w => w.Contains("自动调整")), "提示速度已自动调整");
        Check(Playable(fast.Body, fast.Bpm), "降速后可通过最短音校验");
        Check(single.LoweredFromBpm == null && single.Bpm == 120, "正常曲目不动速度");
        var tiny = new Track();
        tiny.Add(0, 10, 60); tiny.Add(10, 480, 62);
        Check(MidiImporter.Import(Build(0, 480, tiny), "tiny").Warnings.Any(w => w.Contains("20 BPM")), "极短音提示");

        // Unsupported or unusable files are rejected with a readable reason.
        Check(Throws(() => MidiImporter.Import(new byte[] { 1, 2, 3, 4 }, "bad")), "拒绝非MIDI数据");
        Check(Throws(() => MidiImporter.Import(Encoding.ASCII.GetBytes("MThd"), "bad")), "拒绝截断文件头");
        Check(Throws(() => MidiImporter.Import(Build(2, 480, basic), "f2")), "拒绝Format 2");
        Check(Throws(() => MidiImporter.Import(Build(-1, 480, basic), "smpte")), "拒绝SMPTE时间格式");
        var drums = new Track();
        drums.Add(0, 480, 40, 9);
        Check(Throws(() => MidiImporter.Import(Build(0, 480, drums), "drums")), "仅打击乐时报错");
        var silent = new Track();
        Check(Throws(() => MidiImporter.Import(Build(0, 480, silent), "silent")), "没有音符时报错");

        // Round trip through the exporter: pitches and tempo must survive.
        var timeline = ScoreTimeline.Create("1 2_ 3 【1】 #1 0 5", "120", "20");
        var roundTrip = MidiImporter.Import(MidiExporter.Encode(timeline, "往返"), "roundtrip");
        Check(roundTrip.Title == "往返" && roundTrip.Bpm == 120, "往返标题与速度");
        var back = ScoreTimeline.Create(roundTrip.Body, roundTrip.Bpm.ToString(), "20");
        Check(back.Notes.Select(n => n.MidiPitch).SequenceEqual(timeline.Notes.Select(n => n.MidiPitch)),
            $"往返音高一致 [{string.Join(",", back.Notes.Select(n => n.MidiPitch))}] 期望[{string.Join(",", timeline.Notes.Select(n => n.MidiPitch))}] 正文[{roundTrip.Body.Replace("\n", " / ")}]");
        Check(Math.Abs(back.DurationMs - timeline.DurationMs) < 150, "往返总时长接近");

        // Reading from disk keeps the same result.
        string directory = Path.Combine(Path.GetTempPath(), "HarmonicaMidiIn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "曲子.mid");
            File.WriteAllBytes(path, Build(0, 480, basic));
            Check(MidiImporter.Read(path).Title == "测试曲", "从文件读取");
        }
        finally { Directory.Delete(directory, true); }

        Console.WriteLine($"PASS v0.3.4 MIDI import: {passed} tests");
    }

    private sealed class Track
    {
        public string? Name;
        public int? Tempo;
        public bool RunningStatus;
        public bool ZeroVelocityOff;
        public List<(long Start, long Length, int Pitch, int Channel)> Notes { get; } = new();
        public void Add(long start, long length, int pitch, int channel = 0) => Notes.Add((start, length, pitch, channel));
    }

    private static byte[] Build(int format, int division, params Track[] tracks)
    {
        using var file = new MemoryStream();
        static void Write16(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value);
        }
        static void Write32(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 24)); stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value);
        }
        file.Write(Encoding.ASCII.GetBytes("MThd"));
        Write32(file, 6); Write16(file, format); Write16(file, tracks.Length); Write16(file, division);
        foreach (var track in tracks)
        {
            using var body = new MemoryStream();
            long previous = 0;
            void Variable(long value)
            {
                var bytes = new List<byte> { (byte)(value & 0x7F) };
                while ((value >>= 7) > 0) bytes.Insert(0, (byte)((value & 0x7F) | 0x80));
                body.Write(bytes.ToArray(), 0, bytes.Count);
            }
            void At(long tick) { Variable(tick - previous); previous = tick; }
            void Meta(int type, byte[] payload)
            {
                At(0); body.WriteByte(0xFF); body.WriteByte((byte)type);
                Variable(payload.Length); body.Write(payload, 0, payload.Length);
            }
            if (!string.IsNullOrEmpty(track.Name)) Meta(3, Encoding.UTF8.GetBytes(track.Name));
            if (track.Tempo is int tempo)
                Meta(0x51, new[] { (byte)(tempo >> 16), (byte)(tempo >> 8), (byte)tempo });
            var events = new List<(long Tick, int Order, int Status, int Data1, int Data2)>();
            foreach (var note in track.Notes)
            {
                events.Add((note.Start, 1, 0x90 | note.Channel, note.Pitch, 96));
                events.Add((note.Start + note.Length, 0,
                    track.ZeroVelocityOff ? 0x90 | note.Channel : 0x80 | note.Channel, note.Pitch, 0));
            }
            int? running = null;
            foreach (var e in events.OrderBy(e => e.Tick).ThenBy(e => e.Order))
            {
                At(e.Tick);
                if (!track.RunningStatus || running != e.Status || e.Status >= 0xF0)
                { body.WriteByte((byte)e.Status); running = e.Status; }
                body.WriteByte((byte)e.Data1); body.WriteByte((byte)e.Data2);
            }
            At(previous); body.WriteByte(0xFF); body.WriteByte(0x2F); body.WriteByte(0);
            file.Write(Encoding.ASCII.GetBytes("MTrk")); Write32(file, (int)body.Length);
            body.Position = 0; body.CopyTo(file);
        }
        return file.ToArray();
    }
}
