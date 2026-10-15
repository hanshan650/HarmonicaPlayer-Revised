using HarmonicaPlayer;

static class AudioToScoreInstallerTests
{
    public static void Run()
    {
        int count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); count++; }

        // ---------- 体积估算与人读格式 ----------
        Check(AudioToScoreInstaller.DownloadBytes(true) > AudioToScoreInstaller.DownloadBytes(false),
            "GPU 版下载量大于 CPU 版");
        Check(AudioToScoreInstaller.DownloadBytes(false)
                == AudioToScoreInstaller.RuntimeBytes + AudioToScoreInstaller.PipBytes
                   + AudioToScoreInstaller.CommonDepsBytes + AudioToScoreInstaller.TorchCpuBytes
                   + AudioToScoreInstaller.ModelsBytes,
            "下载量由各段相加得出");
        Check(AudioToScoreInstaller.Human(512) == "512 B", "人读格式：字节");
        Check(AudioToScoreInstaller.Human(11_249_023).Contains("MB"), "人读格式：MB");
        Check(AudioToScoreInstaller.Human(2_950_000_000).Contains("GB"), "人读格式：GB");

        // ---------- "1.2 MB" → 字节 ----------
        Check(AudioToScoreInstaller.TryParseSize("218.1 MB", out long mb) && mb > 228_000_000 && mb < 229_000_000,
            "解析 218.1 MB");
        Check(AudioToScoreInstaller.TryParseSize("2.4 GB", out long gb) && gb > 2_570_000_000 && gb < 2_580_000_000,
            "解析 2.4 GB");
        Check(AudioToScoreInstaller.TryParseSize("1.2 MB", out long kb) && kb == 1_258_291, "解析 1.2 MB");
        Check(!AudioToScoreInstaller.TryParseSize("", out _) && !AudioToScoreInstaller.TryParseSize("1.2", out _)
            && !AudioToScoreInstaller.TryParseSize("abc MB", out _) && !AudioToScoreInstaller.TryParseSize("1.2 TB", out _),
            "拒绝非法尺寸");

        // ---------- 侧车 PROGRESS 协议 ----------
        Check(AudioToScoreInstaller.TryParseProgress("PROGRESS 42 下载 SheetSage2", 74, 98, 0, 0, 0,
                out int p42, out string? c42, out _, out _) && p42 == 84 && c42 == "下载 SheetSage2",
            "PROGRESS 行映射到阶段区间");
        Check(AudioToScoreInstaller.TryParseProgress("PROGRESS 0", 74, 98, 0, 0, 0, out int p0, out _, out _, out _)
                && p0 == 74,
            "PROGRESS 0 落在区间起点");
        Check(AudioToScoreInstaller.TryParseProgress("PROGRESS 100 模型下载完成", 74, 98, 0, 0, 0, out int p100, out _, out _, out _)
                && p100 == 98,
            "PROGRESS 100 落在区间终点");

        // ---------- pip 进度行 ----------
        Check(AudioToScoreInstaller.TryParseProgress("Progress 10485760 of 2399172608", 31, 72, 2_950_000_000, 0, 0,
                out int pp, out _, out _, out _) && pp >= 31 && pp <= 72,
            "pip Progress 行折算成阶段百分比");
        Check(AudioToScoreInstaller.TryParseProgress("Downloading chardet-7.6.0-cp311-cp311-win_amd64.whl (1.2 MB)",
                12, 30, 130_000_000, 0, 0, out _, out string? dc, out _, out long cur)
                && dc != null && dc.Contains("1.2 MB") && cur == 1_258_291,
            "Downloading 行给出文件名与当前文件大小");
        // 第二个 Downloading 行到来时，上一个文件的字节要累计到 done。
        Check(AudioToScoreInstaller.TryParseProgress("Downloading torch-2.8.0.whl (2.3 GB)",
                31, 72, 2_950_000_000, 100, 50, out _, out _, out long nd, out long nc)
                && nd == 150 && nc == (long)(2.3 * 1024 * 1024 * 1024),
            "新文件开始时累计上一个文件");

        // ---------- 章节行只刷新说明 ----------
        Check(AudioToScoreInstaller.TryParseProgress("== MERT-v2-FullSong（来源 modelscope）", 74, 98, 0, 0, 0,
                out int ps, out string? cs, out _, out _) && ps == -1 && cs != null && cs.Contains("MERT"),
            "章节行只改说明不报百分比");

        // ---------- 默认安装位置恰好落在自动识别的候选里 ----------
        string fakeBase = Path.Combine(Path.GetTempPath(), "FakeSoftware-" + Guid.NewGuid().ToString("N"));
        string defaultRoot = AudioToScoreInstaller.DefaultRoot(fakeBase);
        Check(defaultRoot == Path.Combine(Path.GetFullPath(fakeBase), AudioToScore.FolderName),
            "默认安装位置是 EXE 旁的「音频转谱」");
        Check(AudioToScore.CandidateRoots(fakeBase, null)
                .Any(r => string.Equals(r, defaultRoot, StringComparison.OrdinalIgnoreCase)),
            "默认安装位置会被自动识别命中");

        Console.WriteLine($"PASS: AudioToScoreInstaller {count} tests");
    }
}
