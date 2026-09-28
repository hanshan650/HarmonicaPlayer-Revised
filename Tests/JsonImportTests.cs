using HarmonicaPlayer;

// Independent checks for the JSON importer (GTIartist song files and any object
// with a notes array of pitch/start/duration).
static class JsonImportTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("JSON 导入: " + name); passed++; }
        static bool Throws(Action action)
        {
            try { action(); } catch (FormatException) { return true; } catch (IOException) { return true; }
            return false;
        }
        static int[] Pitches(string body) => ScoreParser.Parse(body).Select(n => ScoreTimeline.Pitch(n)).ToArray();
        static double[] Beats(string body) => ScoreParser.Parse(body).Select(n => n.Beats).ToArray();
        static bool Playable(string body, int bpm, string gap = "20")
        {
            try { PlaybackValidation.ValidateGap(gap, ScoreParser.Parse(body), 60000.0 / bpm); return true; }
            catch (FormatException) { return false; }
        }

        // GTIartist layout: start/duration in seconds, no BPM field.
        const string basic = """
        {
          "format": "gtiartist-score-v1",
          "title": "测试曲",
          "tracks": { "1": "MIDI Out" },
          "notes": [
            { "pitch": 60, "start": 0.0, "duration": 0.5, "velocity": 100, "track": 1, "phrase_end": false },
            { "pitch": 62, "start": 0.5, "duration": 0.25, "track": 1 },
            { "pitch": 64, "start": 0.75, "duration": 0.25, "track": 1 },
            { "pitch": 72, "start": 1.0, "duration": 0.5, "track": 1 }
          ]
        }
        """;
        var result = JsonImporter.Import(basic, "fallback");
        Check(result.Title == "测试曲" && result.Bpm == 120, $"标题与推断速度（{result.Bpm}）");
        Check(Pitches(result.Body).SequenceEqual(new[] { 60, 62, 64, 72 }), "音高映射");
        Check(Beats(result.Body).SequenceEqual(new[] { 1.0, 0.5, 0.5, 1.0 }), "秒→拍换算");
        Check(result.Warnings.Any(w => w.Contains("来源 JSON")), "提示来源文件");
        Check(!result.Warnings.Any(w => w.Contains("八度")), "本曲不需要移调");

        // The user's file shape: five duplicated tracks, 1/16-beat grid at 121 BPM.
        var duplicate = new System.Text.StringBuilder();
        duplicate.Append("""{ "format": "gtiartist-score-v1", "title": "多轨", "notes": [""");
        for (int track = 2; track <= 4; track++)
            duplicate.Append($$"""{ "pitch": 64, "start": 0.0, "duration": 0.247934, "track": {{track}} },""");
        duplicate.Append("""{ "pitch": 52, "start": 0.0, "duration": 0.743802, "track": 2 } ] }""");
        var duplicated = JsonImporter.Import(duplicate.ToString(), null);
        Check(duplicated.Title == "多轨" && Pitches(duplicated.Body).SequenceEqual(new[] { 64 }),
            "同音高重复轨合并为一次发音");
        Check(duplicated.Warnings.Any(w => w.Contains("音轨")), "提示选择的音轨");

        // Multi-track scores are arrangements: only the track with the most notes plays.
        const string chordJson = """
        {
          "format": "gtiartist-score-v1", "title": "和弦",
          "notes": [
            { "pitch": 60, "start": 0.0, "duration": 0.5, "track": 1 },
            { "pitch": 67, "start": 0.0, "duration": 0.5, "track": 2 },
            { "pitch": 55, "start": 0.5, "duration": 0.5, "track": 1 }
          ]
        }
        """;
        var chord = JsonImporter.Import(chordJson, null);
        Check(Pitches(chord.Body).SequenceEqual(new[] { 60, 55 }), "多轨只演奏音符最多的音轨");
        Check(chord.Warnings.Any(w => w.Contains("音轨")), "提示已选音轨");

        // Overlaps inside the chosen track still keep the highest pitch.
        const string overlapJson = """
        { "title": "同轨重叠", "notes": [
          { "pitch": 60, "start": 0.0, "duration": 0.5, "track": 1 },
          { "pitch": 67, "start": 0.0, "duration": 0.5, "track": 1 },
          { "pitch": 55, "start": 0.5, "duration": 0.5, "track": 1 } ] }
        """;
        var overlapTracks = JsonImporter.Import(overlapJson, null);
        Check(Pitches(overlapTracks.Body).SequenceEqual(new[] { 67, 55 }), "同轨重叠取最高音");
        Check(overlapTracks.Warnings.Any(w => w.Contains("最高音")), "提示同轨合并");

        // 曲库里的分声部曲谱：伴奏轨音符更多但编号更大，默认仍取编号最小的主旋律轨；
        // 界面可先调用 ReadTracks/ListTracks 列给用户选，再用编号指定。
        var listed = JsonImporter.ListTracks(chordJson);
        Check(listed.Count == 2 && listed[0].Label == "1" && listed[0].Notes == 2, "默认音轨=编号最小的音轨");
        Check(listed[1].Label == "2" && listed[1].Notes == 1, "列出其余音轨");
        Check(listed[0].Describe().Contains("音轨1") && listed[0].Describe().Contains("个音"), "音轨说明文字");
        Check(JsonImporter.Import(chordJson, null, "2").Body.Contains("5"), "按编号指定音轨");
        const string arrangement = """
        { "title": "分声部", "tracks": { "1": "Piano", "2": "Piano" }, "notes": [
          { "pitch": 78, "start": 0.0, "duration": 0.5, "track": 1 },
          { "pitch": 80, "start": 0.5, "duration": 0.5, "track": 1 },
          { "pitch": 47, "start": 0.0, "duration": 0.25, "track": 2 },
          { "pitch": 35, "start": 0.0, "duration": 0.25, "track": 2 },
          { "pitch": 47, "start": 0.25, "duration": 0.25, "track": 2 },
          { "pitch": 35, "start": 0.25, "duration": 0.25, "track": 2 } ] }
        """;
        var arrangementResult = JsonImporter.Import(arrangement, null);
        Check(Pitches(arrangementResult.Body).SequenceEqual(new[] { 78, 80 }), "多轨默认取编号最小的音轨（主旋律）");
        Check(JsonImporter.ListTracks(arrangement)[1].ChordNotes == 2, "统计同时发声的和弦");
        Check(JsonImporter.Import(arrangement, null, "2").Warnings.Any(w => w.Contains("音轨")), "提示已演奏的音轨");

        // A later note wins even when it is lower, so a sustained voice cannot swallow the
        // line running above it (and every onset keeps its original time).
        const string legatoJson = """
        { "title": "连奏", "notes": [
          { "pitch": 72, "start": 0.0, "duration": 1.0, "track": 1 },
          { "pitch": 60, "start": 0.5, "duration": 0.25, "track": 1 },
          { "pitch": 62, "start": 0.75, "duration": 0.25, "track": 1 } ] }
        """;
        var legato = JsonImporter.Import(legatoJson, null);
        Check(Pitches(legato.Body).SequenceEqual(new[] { 72, 60, 62 }), "后起的低音不被长音吞掉");
        Check(Beats(legato.Body).SequenceEqual(new[] { 1.0, 0.5, 0.5 }), "截断后时值仍与原文件一致");

        // Range handling: whole-octave shift first, folding only when unavoidable.
        const string low = """
        { "title": "低音", "notes": [
          { "pitch": 36, "start": 0.0, "duration": 0.5 },
          { "pitch": 40, "start": 0.5, "duration": 0.5 } ] }
        """;
        var lowResult = JsonImporter.Import(low, null);
        Check(Pitches(lowResult.Body).SequenceEqual(new[] { 48, 52 }), "低音整体升八度");
        Check(lowResult.Warnings.Any(w => w.Contains("八度")), "提示整体移调");
        const string spread = """
        { "title": "跨域", "notes": [
          { "pitch": 48, "start": 0.0, "duration": 0.5 },
          { "pitch": 96, "start": 0.5, "duration": 0.5 } ] }
        """;
        var spreadResult = JsonImporter.Import(spread, null);
        Check(Pitches(spreadResult.Body).SequenceEqual(new[] { 48, 84 }), "超范围音折回八度");
        Check(spreadResult.Warnings.Any(w => w.Contains("折回")), "提示折回");

        // Rests come from gaps; tiny gaps join the previous note.
        const string gaps = """
        { "title": "休止", "notes": [
          { "pitch": 60, "start": 0.0, "duration": 0.25 },
          { "pitch": 62, "start": 0.5, "duration": 0.25 },
          { "pitch": 64, "start": 1.25, "duration": 0.25 } ] }
        """;
        var gapResult = JsonImporter.Import(gaps, null);
        Check(Beats(gapResult.Body).SequenceEqual(new[] { 0.5, 0.5, 0.5, 1.0, 0.5 }),
            $"空隙转休止 [{string.Join(",", Beats(gapResult.Body))}] @{gapResult.Bpm}BPM");

        // Fidelity first: a note the game cannot hold keeps the file's own tempo and is only
        // reported, so every other note keeps its original time.
        const string quick = """
        { "title": "极短", "notes": [
          { "pitch": 60, "start": 0.0, "duration": 0.03125 },
          { "pitch": 62, "start": 0.03125, "duration": 0.46875 } ] }
        """;
        var quickResult = JsonImporter.Import(quick, null);
        Check(quickResult.Bpm == 120 && quickResult.LoweredFromBpm == null,
            $"短音不再降速（{quickResult.Bpm} BPM）");
        Check(quickResult.Warnings.Any(w => w.Contains("按住")), "提示短音按不住");
        Check(Beats(quickResult.Body)[0] == 0.0625, "极短音的时值未被拉长");
        Check(Playable(quickResult.Body, quickResult.Bpm), "保真导入后可正常开始演奏");
        Check(result.LoweredFromBpm == null && result.Bpm == 120, "正常曲目不动速度");

        // A bpm field switches start/duration to beats.
        const string withBpm = """
        { "title": "拍数版", "bpm": 120, "notes": [
          { "pitch": 60, "start": 0, "duration": 1 },
          { "pitch": 62, "start": 1, "duration": 0.5 } ] }
        """;
        var beatsMode = JsonImporter.Import(withBpm, null);
        Check(beatsMode.Bpm == 120 && Beats(beatsMode.Body).SequenceEqual(new[] { 1.0, 0.5 }), "按拍数解析");
        Check(beatsMode.Warnings.Any(w => w.Contains("拍数")), "提示按拍数处理");

        // Unknown formats are still read when the notes array matches, with a warning.
        const string unknown = """
        { "format": "other-v1", "title": "别的格式", "notes": [
          { "pitch": "60", "start": "0", "duration": "1" } ] }
        """;
        var unknownResult = JsonImporter.Import(unknown, null);
        Check(unknownResult.Title == "别的格式" && Pitches(unknownResult.Body).SequenceEqual(new[] { 60 }),
            "未识别 format 仍按字段解析（含字符串数值）");
        Check(unknownResult.Warnings.Any(w => w.Contains("未识别")), "提示未识别 format");

        // Missing or broken content is rejected with a readable reason.
        Check(Throws(() => JsonImporter.Import("{", "bad")), "拒绝非法 JSON");
        Check(Throws(() => JsonImporter.Import("[1,2,3]", "bad")), "拒绝非对象根节点");
        Check(Throws(() => JsonImporter.Import("""{ "format": "gtiartist-score-v1" }""", "bad")), "缺少 notes 时报错");
        Check(Throws(() => JsonImporter.Import("""{ "notes": [] }""", "bad")), "notes 为空时报错");
        Check(Throws(() => JsonImporter.Import("""{ "notes": [ { "pitch": 60 } ] }""", "bad")), "缺少字段时报错");
        var skipped = JsonImporter.Import("""
        { "title": "跳过", "notes": [ { "pitch": 60, "start": 0, "duration": 1 }, { "pitch": 60 }, { "pitch": 90, "start": 1, "duration": 0 } ] }
        """, null);
        Check(skipped.Warnings.Any(w => w.Contains("已忽略2条")), "提示忽略无效记录");
        var noTitle = JsonImporter.Import("""
        { "notes": [ { "pitch": 60, "start": 0, "duration": 1 } ] }
        """, "文件名标题");
        Check(noTitle.Title == "文件名标题", "无 title 时使用文件名");

        // Reading from disk keeps the same result.
        string directory = Path.Combine(Path.GetTempPath(), "HarmonicaJson-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "song.json");
            File.WriteAllText(path, basic, new System.Text.UTF8Encoding(false));
            Check(JsonImporter.Read(path).Title == "测试曲", "从文件读取");
        }
        finally { Directory.Delete(directory, true); }

        Console.WriteLine($"PASS v0.3.5 JSON import: {passed} tests");
    }
}
