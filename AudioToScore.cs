using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace HarmonicaPlayer;

// 「音频转谱」附加组件的驱动。
//
// 这个组件刻意**不进分发包**：SheetSage2 + MERT-v2-FullSong 权重约 2.6 GB，
// Python/PyTorch 环境再加约 6.8 GB，合计约 9.3 GB，塞进现在 163 MB 的单文件 EXE 里没有意义。
// 用户自己把环境装好之后，主程序只做三件事：找到它、起进程、把产出的 MIDI 接进现有导入管线。
//
// 刻意与 WPF 无关（与 ScoreEditor 同一路数），这样测试可以直接对协议解析与目录判定下断言。
public static class AudioToScore
{
    /// 运行时目录里必须存在的相对路径。
    public const string PythonRelative = @"venv\Scripts\python.exe";
    public const string ScriptRelative = "transcribe.py";
    public const string SheetSageWeights = @"models\SheetSage2\model.safetensors";
    public const string MertWeights = @"models\MERT-v2-FullSong\model.safetensors";

    /// 指认环境目录的环境变量；设置文件里的值优先级次之。
    public const string EnvironmentVariable = "HARMONICA_AUDIO2SCORE";
    /// EXE 同级的默认文件夹名，也是 setup.ps1 建议安装的位置之一。
    public const string FolderName = "音频转谱";
    /// 仓库开发布局下的同级目录名（EXE 在 bin/Release/net8.0-windows 里）。
    public const string SiblingRuntimeName = "AudioToScore-runtime";

    /// 从 EXE 目录往上走几层能到仓库的父目录：net8.0-windows → Release → bin → 仓库。
    private const int RepositoryParentDepth = 3;

    /// 侧车写出的 summary.json。字段缺失一律给默认值——模型换版本时不该让界面炸掉。
    public sealed record Summary(
        bool Ok,
        string MelodyFile,
        string OutDir,
        double DurationSec,
        double ElapsedSec,
        string Device,
        string DeviceName,
        string Dtype,
        int PeakVramMib,
        int MelodyNotes,
        int VocalNotes,
        int InstrumentalNotes,
        int Events,
        string MelodyTrack,
        string? AbcError,
        IReadOnlyList<string> Warnings)
    {
        /// 相对时长的倍率，越大越快；<=0 表示无法计算。
        public double RealtimeFactor => ElapsedSec > 0 ? DurationSec / ElapsedSec : 0;

        public string TrackLabel => MelodyTrack switch
        {
            "vocal" => "人声主旋律",
            "instrumental" => "器乐主旋律",
            _ => "主旋律"
        };
    }

    // ---------- summary.json ----------

    public static bool TryParseSummary(string json, out Summary? summary, out string? error)
    {
        summary = null;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "summary.json 不是对象。"; return false; }

            string Text(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? "" : "";
            double Number(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                    ? value.GetDouble() : 0;
            int Integer(string name) => (int)Math.Round(Number(name));
            // ok 缺失时按“有 melody_file 就算成功”处理，避免旧版侧车被判成失败。
            bool Ok() => root.TryGetProperty("ok", out var value) && value.ValueKind == JsonValueKind.False
                ? false : root.TryGetProperty("melody_file", out _);

            var warnings = new List<string>();
            if (root.TryGetProperty("warnings", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) warnings.Add(item.GetString()!);

            summary = new Summary(
                Ok(), Text("melody_file"), Text("out_dir"), Number("duration_sec"), Number("elapsed_sec"),
                Text("device"), Text("device_name"), Text("dtype"), Integer("peak_vram_mib"),
                Integer("melody_notes"), Integer("vocal_notes"), Integer("instrumental_notes"),
                Integer("events"), Text("melody_track"),
                root.TryGetProperty("abc_error", out var abc) && abc.ValueKind == JsonValueKind.String
                    ? abc.GetString() : null,
                warnings);
            if (!summary.Ok) { error = "侧车报告转谱失败。"; return false; }
            if (string.IsNullOrEmpty(summary.MelodyFile) || !File.Exists(summary.MelodyFile))
            {
                error = "侧车没有产出 MIDI 文件。";
                return false;
            }
            return true;
        }
        catch (JsonException e) { error = "summary.json 解析失败：" + e.Message; return false; }
    }

    // ---------- 环境定位 ----------

    /// 依次尝试的候选目录。顺序即优先级：显式配置 > EXE 旁 > 仓库布局 > 用户目录。
    public static IReadOnlyList<string> CandidateRoots(string baseDirectory, string? configured)
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path); } catch (Exception) { return; }
            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
        }

        Add(Environment.GetEnvironmentVariable(EnvironmentVariable));
        Add(configured);
        Add(Path.Combine(baseDirectory, FolderName));
        Add(Path.Combine(SiblingOfRepository(baseDirectory), SiblingRuntimeName));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HarmonicaPlayer", SiblingRuntimeName));
        return roots;
    }

    /// EXE 在 bin/<配置>/<框架>/ 下时，返回仓库的父目录；布局不符就返回 EXE 目录本身。
    private static string SiblingOfRepository(string baseDirectory)
    {
        var info = new DirectoryInfo(baseDirectory);
        for (int i = 0; i < RepositoryParentDepth && info.Parent != null; i++) info = info.Parent;
        return info.FullName;
    }

    /// 只检查关键文件是否齐全（不跑进程），返回原因供界面提示。
    public static bool TryLocate(string baseDirectory, string? configured, out string root, out string reason,
        out IReadOnlyList<string> candidates)
    {
        candidates = CandidateRoots(baseDirectory, configured);
        foreach (var candidate in candidates)
        {
            reason = Describe(candidate);
            if (reason.Length == 0) { root = candidate; return true; }
        }
        root = candidates.Count > 0 ? candidates[0] : baseDirectory;
        reason = candidates.Count == 0
            ? "没有可用的候选目录。"
            : Describe(candidates[0]);
        return false;
    }

    /// 返回空串表示这个目录可以直接用；否则是人话说明缺什么。
    public static string Describe(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return "目录不存在：" + root;
        if (!File.Exists(Path.Combine(root, PythonRelative))) return "缺少 Python 环境（venv）。";
        if (!File.Exists(Path.Combine(root, ScriptRelative))) return "缺少 transcribe.py。";
        if (!File.Exists(Path.Combine(root, SheetSageWeights))) return "缺少 SheetSage2 权重。";
        if (!File.Exists(Path.Combine(root, MertWeights))) return "缺少 MERT-v2-FullSong 权重。";
        return "";
    }

    /// 界面里显示的「去哪儿装」提示。
    public static string SetupHint(string baseDirectory) => string.Join("\n",
        "音频转谱是一个可选附加组件，不随分发包提供（模型与环境约 9 GB）。",
        "装上之后本程序会自动找到它；也可以设置环境变量 " + EnvironmentVariable + " 指认目录。",
        "",
        "安装步骤：",
        "1. 打开仓库里的 extras\\audio-to-score 文件夹；",
        "2. 右键 setup.ps1 → 使用 PowerShell 运行（首次需下载约 6 GB）；",
        "3. 装完回到这里再点一次「音频转谱」。",
        "",
        "候选目录（按顺序找）：",
        string.Join("\n", CandidateRoots(baseDirectory, null).Select(r => "  · " + r)));

    // ---------- 起进程 ----------

    /// 转谱结果，以及侧车原始输出的尾巴（出错时用来给用户看原因）。
    public sealed record RunResult(Summary? Summary, string Tail);

    /// 跑一次转谱。progress 收到（百分比, 说明），以及原样的日志行。
    public static async Task<RunResult> RunAsync(
        string root, string audioPath, string outDir,
        Action<int, string>? progress, Action<string>? log, CancellationToken token)
    {
        string python = Path.Combine(root, PythonRelative);
        string script = Path.Combine(root, ScriptRelative);
        string modelRoot = Path.Combine(root, "models");
        Directory.CreateDirectory(outDir);

        var arguments = new List<string>
        {
            script,
            "--audio", audioPath,
            "--out", outDir,
            "--models-root", modelRoot,
            "--device", "auto",
            "--dtype", "auto",
            "--melody-track", "auto"
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        // 不设这个的话，重定向后的 Python 会用系统 ANSI 代码页输出，中文全变乱码。
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["HF_HUB_OFFLINE"] = "1";
        startInfo.Environment["TRANSFORMERS_OFFLINE"] = "1";

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var tail = new Queue<string>();
        string? summaryPath = null;
        string? failure = null;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { } line) return;
            if (line.StartsWith("PROGRESS ", StringComparison.Ordinal))
            {
                var rest = line["PROGRESS ".Length..];
                int space = rest.IndexOf(' ');
                string number = space < 0 ? rest : rest[..space];
                string text = space < 0 ? "" : rest[(space + 1)..];
                if (int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent))
                    progress?.Invoke(percent, text);
                return;
            }
            if (line.StartsWith("DONE ", StringComparison.Ordinal)) { summaryPath = line["DONE ".Length..].Trim(); return; }
            if (line.StartsWith("ERROR ", StringComparison.Ordinal)) { failure = line["ERROR ".Length..].Trim(); return; }

            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 40) tail.Dequeue();
            }
            log?.Invoke(line);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { } line) return;
            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 40) tail.Dequeue();
            }
            log?.Invoke(line);
        };

        if (!process.Start()) throw new InvalidOperationException("无法启动转谱进程。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* 进程可能已经退出 */ }
            throw;
        }
        // WaitForExitAsync 只等进程结束，异步的 OutputDataReceived 可能还没投递完；
        // 再等一次同步版，把管道里剩下的行收干净，否则最后的 DONE 行会丢。
        process.WaitForExit();

        string joinedTail;
        lock (tail) joinedTail = string.Join("\n", tail);
        if (failure != null) return new RunResult(null, failure + "\n" + joinedTail);
        if (process.ExitCode != 0)
            return new RunResult(null, $"转谱进程退出码 {process.ExitCode}。\n{joinedTail}");
        if (summaryPath == null || !File.Exists(summaryPath))
            return new RunResult(null, "转谱进程没有报告结果。\n" + joinedTail);

        if (!TryParseSummary(await File.ReadAllTextAsync(summaryPath, token), out var summary, out string? error))
            return new RunResult(null, error + "\n" + joinedTail);
        return new RunResult(summary, joinedTail);
    }

    /// 跑 `--check` 拿到环境自检信息（torch 版本、显卡、权重是否齐全）。
    public static async Task<string> ProbeAsync(string root, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(root, PythonRelative),
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(Path.Combine(root, ScriptRelative));
        startInfo.ArgumentList.Add("--check");
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["HF_HUB_OFFLINE"] = "1";
        startInfo.Environment["TRANSFORMERS_OFFLINE"] = "1";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动自检进程。");
        string output = await process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return output.Trim();
    }
}
