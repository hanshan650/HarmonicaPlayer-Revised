using System.Text.Json;
using HarmonicaPlayer;

static class AudioToScoreTests
{
    public static void Run()
    {
        int count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); count++; }

        string sandbox = Path.Combine(Path.GetTempPath(), "AudioToScoreTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            // ---------- summary.json 解析 ----------
            string melody = Path.Combine(sandbox, "melody.mid");
            File.WriteAllBytes(melody, new byte[] { 0x4D, 0x54, 0x68, 0x64 });

            string SummaryJson(string melodyPath, params string[] extra) => JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["melody_file"] = melodyPath,
                ["out_dir"] = sandbox,
                ["duration_sec"] = 372.4,
                ["elapsed_sec"] = 41.2,
                ["device"] = "cuda",
                ["device_name"] = "NVIDIA GeForce RTX 4060 Laptop GPU",
                ["dtype"] = "bf16",
                ["peak_vram_mib"] = 3120,
                ["melody_notes"] = 812,
                ["vocal_notes"] = 800,
                ["instrumental_notes"] = 12,
                ["events"] = 430,
                ["melody_track"] = "vocal",
                ["abc_error"] = null,
                ["warnings"] = extra
            });
            string Good(params string[] extra) => SummaryJson(melody, extra);

            Check(AudioToScore.TryParseSummary(Good(), out var summary, out _) && summary != null,
                "完整 summary 可解析");
            Check(summary!.MelodyNotes == 812 && summary.InstrumentalNotes == 12 && summary.Events == 430,
                "统计字段取值正确");
            Check(Math.Abs(summary.RealtimeFactor - 372.4 / 41.2) < 1e-6, "倍速按 时长/耗时 计算");
            Check(summary.TrackLabel == "人声主旋律", "人声轨标签");
            Check(summary.Dtype == "bf16" && summary.PeakVramMib == 3120, "dtype 与显存峰值");

            Check(AudioToScore.TryParseSummary(Good("短音频可能缺少节拍信息"), out var warned, out _) &&
                  warned!.Warnings.Count == 1 && warned.Warnings[0].StartsWith("短音频"),
                "warnings 原样带回");

            // 字段缺失不该抛异常，一律取默认值。
            Check(AudioToScore.TryParseSummary(
                    JsonSerializer.Serialize(new Dictionary<string, object?> { ["melody_file"] = melody }),
                    out var minimal, out _) &&
                  minimal!.ElapsedSec == 0 && minimal.Device == "" && minimal.Warnings.Count == 0,
                "字段缺失时用默认值且不报错");
            Check(minimal!.RealtimeFactor == 0, "耗时缺失时倍速为 0 而不是除零");

            Check(!AudioToScore.TryParseSummary("不是 JSON", out _, out string? broken) && broken != null,
                "非法 JSON 被拒绝并给出原因");
            Check(!AudioToScore.TryParseSummary(
                    JsonSerializer.Serialize(new Dictionary<string, object?> { ["ok"] = false, ["melody_file"] = melody }),
                    out _, out string? failed) && failed!.Contains("失败"),
                "ok=false 视为失败");
            Check(!AudioToScore.TryParseSummary(SummaryJson(Path.Combine(sandbox, "nope.mid")),
                    out _, out string? missing) && missing!.Contains("没有产出"),
                "声明的 MIDI 不存在时判失败");
            // 只写 ok:true 却没写 melody_file，同样不能当成成功。
            Check(!AudioToScore.TryParseSummary(
                    JsonSerializer.Serialize(new Dictionary<string, object?> { ["ok"] = true }),
                    out _, out _),
                "缺少 melody_file 时判失败");

            // ---------- 环境目录判定 ----------
            string incomplete = Path.Combine(sandbox, "empty");
            Directory.CreateDirectory(incomplete);
            Check(AudioToScore.Describe(incomplete).Contains("Python"), "只有空目录时提示缺 Python");
            Check(AudioToScore.Describe(Path.Combine(sandbox, "none")).Contains("不存在"), "目录不存在时说得出来");

            string good = Path.Combine(sandbox, "runtime");
            Directory.CreateDirectory(Path.Combine(good, "venv", "Scripts"));
            Directory.CreateDirectory(Path.Combine(good, "models", "SheetSage2"));
            Directory.CreateDirectory(Path.Combine(good, "models", "MERT-v2-FullSong"));
            File.WriteAllBytes(Path.Combine(good, AudioToScore.PythonRelative), Array.Empty<byte>());
            File.WriteAllText(Path.Combine(good, AudioToScore.ScriptRelative), "");
            File.WriteAllBytes(Path.Combine(good, AudioToScore.SheetSageWeights), Array.Empty<byte>());
            Check(AudioToScore.Describe(good).Contains("MERT"), "缺父模型权重时能指出来");
            File.WriteAllBytes(Path.Combine(good, AudioToScore.MertWeights), Array.Empty<byte>());
            Check(AudioToScore.Describe(good) == "", "四个关键文件齐了就判定可用");

            Check(AudioToScore.TryLocate(sandbox, good, out string found, out string why, out _) && found == good && why == "",
                "显式配置的目录优先且能命中");
            Check(!AudioToScore.TryLocate(sandbox, Path.Combine(sandbox, "none"), out string fallback, out string miss, out _) &&
                  miss.Length > 0,
                "配置错时给出失败原因");
            Check(fallback == Path.Combine(sandbox, "none"), "失败时回报第一个候选目录便于排查");

            var candidates = AudioToScore.CandidateRoots(sandbox, good);
            Check(candidates[0] == Path.GetFullPath(good), "配置的目录排在最前");
            Check(candidates.Contains(Path.Combine(Path.GetFullPath(sandbox), AudioToScore.FolderName)),
                "候选里包含 EXE 旁的默认文件夹");
            Check(candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count() == candidates.Count,
                "候选目录不重复");

            // 同一个目录写两次（配置 + 默认位置重合）时只出现一次。
            var deduped = AudioToScore.CandidateRoots(sandbox, Path.Combine(sandbox, AudioToScore.FolderName));
            Check(deduped.Count(c => c.Equals(Path.Combine(Path.GetFullPath(sandbox), AudioToScore.FolderName),
                      StringComparison.OrdinalIgnoreCase)) == 1,
                "重复候选被去掉");

            string hint = AudioToScore.SetupHint(sandbox);
            Check(hint.Contains(AudioToScore.EnvironmentVariable) && hint.Contains("setup.ps1"),
                "安装提示里给出环境变量名与安装脚本");

            // ---------- 协议行解析（run 协议的关键约定） ----------
            Check(AudioToScore.PythonRelative.Replace('\\', '/').EndsWith("venv/Scripts/python.exe"),
                "Python 相对路径约定不变");
            Check(AudioToScore.ScriptRelative == "transcribe.py" && AudioToScore.FolderName == "音频转谱",
                "脚本名与文件夹名约定不变");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch (Exception) { /* 临时目录清理失败不影响测试结论 */ }
        }

        Console.WriteLine($"PASS: AudioToScore {count} tests");
    }
}
