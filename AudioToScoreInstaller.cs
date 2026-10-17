using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;

namespace HarmonicaPlayer;

// 「音频转谱」附加组件的**应用内安装器**。
//
// 背景：音频转谱需要 Python 运行环境（约 6.8 GB）与模型权重（2.6 GB），
// 把这两样塞进 163 MB 的分发包没有意义，所以改由用户在本机按需获取。
//
// 为什么是嵌入式 Python 而不是让用户自己装一套：
//   venv 布局的 pyvenv.cfg 里写死了创建者的绝对路径（`home = C:\Users\xxx\...`），
//   而且 venv 自身不带 python311.dll，整个目录不可移植，拷给谁都用不了；
//   嵌入式包（python-3.11.9-embed-amd64.zip，11 MB）解压即用、自带 DLL 与标准库、
//   路径全相对，是唯一能"由程序自己造一个 Python"的形态。
//
// 版本为什么锁 3.11：模型的 requirements 把 numpy 锁在 1.24.3，
// 而它只有 3.10/3.11 的 wheel，3.12 以上装不上。
//
// 这个类刻意与 WPF 无关（与 AudioToScore / ScoreEditor 同一路数）：进度与日志都走回调，
// 因此可以直接对体积估算、命令行拼装与输出解析下断言。
public static class AudioToScoreInstaller
{
    // ---------- 要装的东西 ----------

    /// 嵌入式 Python 版本。改这里必须同时改 RuntimePthName 与 RuntimeUrls。
    public const string RuntimeVersion = "3.11.9";
    public const string RuntimePthName = "python311._pth";

    /// Python 嵌入式包，按优先级尝试。
    public static readonly IReadOnlyList<string> RuntimeUrls = new[]
    {
        "https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip",
        "https://mirrors.huaweicloud.com/python/3.11.9/python-3.11.9-embed-amd64.zip",
    };

    /// 给嵌入式 Python 补 pip 用：嵌入式包不含 pip，官方给的标准做法就是它。
    public const string PipBootstrapUrl = "https://bootstrap.pypa.io/get-pip.py";

    /// 通用依赖的索引。
    public const string PyPiMirror = "https://pypi.tuna.tsinghua.edu.cn/simple";

    /// PyTorch 的索引，按优先级。国内镜像实测可用且快得多，官方源兜底。
    public static readonly IReadOnlyList<string> TorchGpuIndexes = new[]
    {
        "https://mirror.sjtu.edu.cn/pytorch-wheels/cu126/",
        "https://download.pytorch.org/whl/cu126",
    };

    /// CUDA 12.8 版索引。RTX 50 系（Blackwell，sm_120）只在 cu128 及以后的
    /// 构建里才有内核；cu126 装得上、初始化也正常，但一发射内核就报
    /// "no kernel image is available"。装完 cu126 会先做内核冒烟测试，
    /// 不兼容时升级到这里的版本。
    public static readonly IReadOnlyList<string> TorchGpu128Indexes = new[]
    {
        "https://mirror.sjtu.edu.cn/pytorch-wheels/cu128/",
        "https://download.pytorch.org/whl/cu128",
    };

    public static readonly IReadOnlyList<string> TorchCpuIndexes = new[]
    {
        "https://download.pytorch.org/whl/cpu",
    };

    /// 给新装的 torch 做真实内核冒烟测试（python -c 直接跑）。
    /// 输出约定：恰好一行 "PROBE CUDA_OK <显卡名>" / "PROBE CUDA_FAIL <错误首行>" / "PROBE CUDA_ABSENT"。
    public const string CudaProbeScript =
        "import torch\r\n" +
        "if not torch.cuda.is_available():\r\n" +
        "    print('PROBE CUDA_ABSENT')\r\n" +
        "else:\r\n" +
        "    try:\r\n" +
        "        x = torch.randn(128, 128, device='cuda', dtype=torch.bfloat16)\r\n" +
        "        (x @ x.mT).float().sum().item()\r\n" +
        "        print('PROBE CUDA_OK ' + torch.cuda.get_device_name(0))\r\n" +
        "    except Exception as exc:\r\n" +
        "        text = str(exc) or type(exc).__name__\r\n" +
        "        print('PROBE CUDA_FAIL ' + text.splitlines()[0])\r\n";

    /// 解析冒烟测试那行输出。status：OK / FAIL / ABSENT；解析不出来返回 false。
    public static bool TryParseCudaProbe(string line, out string status, out string detail)
    {
        status = "";
        detail = "";
        string text = line.Trim();
        if (!text.StartsWith("PROBE CUDA_", StringComparison.Ordinal)) return false;
        string rest = text["PROBE CUDA_".Length..];
        int space = rest.IndexOf(' ');
        string head = space < 0 ? rest : rest[..space];
        if (head is not ("OK" or "FAIL" or "ABSENT")) return false;
        status = head;
        detail = space < 0 ? "" : rest[(space + 1)..].Trim();
        return true;
    }

    /// 版本必须与模型仓库的 requirements.txt 一致：transformers 4.45.2 与 numpy 1.24.3 都是硬约束。
    /// mir_eval 也不能漏：SheetSage2 的 tokenization_sheetsage2.py 会 import mir_eval.chord，
    /// 缺了会在**加载模型时**才炸（初版就漏过这个）。
    public static readonly IReadOnlyList<string> Dependencies = new[]
    {
        "transformers==4.45.2",
        "huggingface-hub==0.36.0",
        "safetensors==0.5.3",
        "numpy==1.24.3",
        "scipy==1.13.1",
        "mir_eval==0.8.2",
        "pretty_midi==0.2.10",
        "mido==1.3.3",
        "setuptools==78.1.1",
        "soundfile",
    };

    // ---------- 体积估算（用于提前告知与磁盘检查） ----------

    public const long RuntimeBytes = 11_249_023;
    public const long PipBytes = 2_230_488;
    /// 除 PyTorch 外的依赖合计。
    public const long CommonDepsBytes = 130_000_000;
    /// torch（含 CUDA 运行库）与 torchaudio 的 wheel 合计，这是最大的一块。
    public const long TorchGpuBytes = 2_950_000_000;
    public const long TorchCpuBytes = 230_000_000;
    /// SheetSage2（218 MB）+ MERT-v2-FullSong（2.4 GB）。
    public const long ModelsBytes = 2_632_000_000;

    /// 安装占用的磁盘。wheel 是压缩态、装完会解压，所以要按展开后算：
    /// 实测装完约 9.3 GB（site-packages 6.8 GB + 权重 2.6 GB）。
    public const long DiskBytes = 12_000_000_000;

    public static long DownloadBytes(bool gpu) =>
        RuntimeBytes + PipBytes + CommonDepsBytes + (gpu ? TorchGpuBytes : TorchCpuBytes) + ModelsBytes;

    public static string Human(long bytes)
    {
        double value = bytes;
        foreach (string unit in new[] { "B", "KB", "MB", "GB" })
        {
            if (value < 1024 || unit == "GB")
                return unit == "B" ? $"{value:0} B" : $"{value:0.0} {unit}";
            value /= 1024;
        }
        return $"{value:0.0} GB";
    }

    // ---------- 计划与前置检查 ----------

    /// 默认装到 EXE 旁的「音频转谱」，正好落在 AudioToScore 的候选目录里，
    /// 装完不需要任何额外配置就能被认出来。
    public static string DefaultRoot(string baseDirectory) => Path.Combine(baseDirectory, AudioToScore.FolderName);

    public static bool HasEnoughDisk(string root, out string reason)
    {
        try
        {
            string drive = Path.GetPathRoot(Path.GetFullPath(root)) ?? "";
            if (drive.Length == 0) { reason = ""; return true; }
            long free = new DriveInfo(drive).AvailableFreeSpace;
            if (free < DiskBytes)
            {
                reason = $"目标磁盘 {drive} 只剩 {Human(free)}，装不下（需要约 {Human(DiskBytes)}）。"
                    + "请换一个位置，例如换到另一个盘。";
                return false;
            }
            reason = "";
            return true;
        }
        catch (Exception e)
        {
            // 查不出来就不拦：磁盘检查只是提前告知，不该成为安装的阻碍。
            reason = "（无法检查磁盘空间：" + e.Message + "）";
            return true;
        }
    }

    /// 机器上有没有 NVIDIA 驱动。有就装 CUDA 版 torch，没有就装 CPU 版，
    /// 免得没独显的用户白下 2.7 GB。
    public static bool DetectNvidiaGpu()
    {
        try
        {
            // nvcuda.dll 是 NVIDIA 驱动的 CUDA 用户态组件，装了驱动就有，不依赖显卡是否亮机。
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "nvcuda.dll");
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------- 安装主流程 ----------

    /// 分阶段推进。百分比是全局的，各阶段领取不重叠的区间，
    /// 免得后一阶段的进度把前面已经走过的进度条拽回去。
    public static async Task InstallAsync(
        string root,
        bool gpu,
        Action<int, string>? progress,
        Action<string>? log,
        CancellationToken token)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);

        int current = 0;
        void Report(int percent, string text)
        {
            // percent < 0 表示"只刷新说明、不动进度条"（例如章节行），
            // 直接钳到 0 会把已经走过的进度拽回起点。
            if (percent >= 0) current = Math.Clamp(percent, 0, 100);
            progress?.Invoke(current, text);
        }

        // 子进程输出 → 原样进日志；顺带解析进度线索刷新进度条。
        // 必须按 \r 再拆一次：pip 的进度是覆盖式输出，整段可能挤在同一"行"里。
        Action<string> Piped(int from, int to, PipState state) => raw =>
        {
            foreach (string part in raw.Split('\r'))
            {
                string text = part.Trim();
                if (text.Length == 0) continue;

                // 纯进度行只喂进度条，不进日志——否则一次安装会刷出几千行。
                bool progressOnly = text.StartsWith("PROGRESS ", StringComparison.Ordinal)
                    || text.StartsWith("Progress ", StringComparison.Ordinal);
                if (!progressOnly) log?.Invoke(text);

                if (TryParseProgress(text, state, from, to, out int percent, out string? caption))
                {
                    if (caption != null) state.Caption = caption;
                    Report(percent, state.Caption);
                }
            }
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HarmonicaPlayer-Revised/1.3");

        // ---------- ① 嵌入式 Python ----------
        if (AudioToScore.FindPython(root) is null)
        {
            Report(2, $"下载 Python 运行环境（{Human(RuntimeBytes)}）");
            string zip = Path.Combine(root, "python-embed.zip");
            await DownloadAsync(http, RuntimeUrls, zip,
                (done, total) => Report(2 + (int)(4 * Ratio(done, total)), "下载 Python 运行环境 " + Percent(done, total)),
                token);

            Report(7, "解压 Python 运行环境");
            ZipFile.ExtractToDirectory(zip, root, overwriteFiles: true);
            File.Delete(zip);
        }
        else
        {
            Report(7, "已有 Python 运行环境，跳过");
        }

        // `_pth` 决定 sys.path：不列 Lib\site-packages，pip 装的东西就 import 不到；
        // 不开 import site，site-packages 下的 .pth 文件（有些包靠它注册路径）不生效。
        Directory.CreateDirectory(Path.Combine(root, "Lib", "site-packages"));
        File.WriteAllText(
            Path.Combine(root, RuntimePthName),
            "python311.zip\r\n.\r\nLib\\site-packages\r\nimport site\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string python = AudioToScore.FindPython(root)
            ?? throw new InvalidOperationException("解压后仍找不到 python.exe，安装包可能不完整。");

        // ---------- ② pip ----------
        Report(9, "准备 pip");
        if (!await HasPipAsync(python, root, token))
        {
            string bootstrap = Path.Combine(root, "get-pip.py");
            await DownloadAsync(http, new[] { PipBootstrapUrl }, bootstrap,
                (done, total) => Report(9, "下载 pip 引导脚本 " + Percent(done, total)), token);
            int code = await RunAsync(python,
                new[] { bootstrap, "--no-warn-script-location", "-i", PyPiMirror }, root, log, token);
            File.Delete(bootstrap);
            if (code != 0) throw new InvalidOperationException($"安装 pip 失败（退出码 {code}）。");
        }
        Report(11, "pip 就绪");

        // ---------- ③ 基础依赖 ----------
        var common = new List<string> { "-m", "pip", "install", "--no-warn-script-location", "-i", PyPiMirror };
        common.AddRange(Dependencies);
        Report(12, "安装基础依赖");
        var commonState = new PipState { Total = CommonDepsBytes, Caption = "安装基础依赖" };
        int depsCode = await RunAsync(python, common, root, Piped(12, 30, commonState), token);
        if (depsCode != 0) throw new InvalidOperationException($"安装基础依赖失败（退出码 {depsCode}）。");

        // ---------- ④ PyTorch ----------
        long torchBytes = gpu ? TorchGpuBytes : TorchCpuBytes;
        IReadOnlyList<string> indexes = gpu ? TorchGpuIndexes : TorchCpuIndexes;
        string label = gpu ? "PyTorch（CUDA 版）" : "PyTorch（CPU 版）";
        Exception? torchError = null;
        foreach (string index in indexes)
        {
            Report(31, $"下载 {label} {Human(torchBytes)}");
            var args = new List<string>
            {
                "-m", "pip", "install", "--no-warn-script-location",
                "torch==2.8.0", "torchaudio==2.8.0",
                "--index-url", index,
            };
            args.Add("--extra-index-url");
            args.Add(PyPiMirror);

            var state = new PipState { Total = torchBytes, Caption = label };
            int code = await RunAsync(python, args, root, Piped(31, 72, state), token);
            if (code == 0) { torchError = null; break; }

            torchError = new InvalidOperationException($"从 {index} 安装 {label} 失败（退出码 {code}）。");
            log?.Invoke("换下一个源重试：" + torchError.Message);
        }
        if (torchError != null) throw torchError;

        // ---------- ④.5 显卡内核兼容性 ----------
        // nvcuda.dll 只能说明"装了 N 卡驱动"，驱动能初始化 CUDA 不代表这套 torch
        // 编译了这张卡的内核（RTX 50 系 + cu126 即如此，is_available() 照样返回 true）。
        // 所以装完必须真发射一次内核验证；不行就 cu128 → CPU 逐级降级。
        if (gpu)
        {
            Report(72, "检查显卡与 PyTorch 的兼容性");
            var first = await RunCaptureAsync(python, new[] { "-c", CudaProbeScript }, root, token);
            string probeLine = first.Output.Split('\n')
                .Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("PROBE CUDA_", StringComparison.Ordinal)) ?? "";
            TryParseCudaProbe(probeLine, out string status, out string detail);

            if (status == "FAIL")
            {
                log?.Invoke("当前 PyTorch 与显卡不兼容（" + detail + "），尝试改装 CUDA 12.8 版…");
                status = await ReinstallTorchAsync(python, root, TorchGpu128Indexes, "CUDA 12.8 版 PyTorch",
                    torchBytes, Piped(31, 72, new PipState { Total = torchBytes }), log, token);
            }
            if (status == "FAIL")
            {
                log?.Invoke("CUDA 12.8 版仍与显卡不兼容，改装 CPU 版（转谱会慢很多，但稳定可用）。");
                status = await ReinstallTorchAsync(python, root, TorchCpuIndexes, "CPU 版 PyTorch",
                    TorchCpuBytes, Piped(31, 72, new PipState { Total = TorchCpuBytes }), log, token);
            }
            if (status == "FAIL")
                log?.Invoke("警告：CPU 版也没能装上，转谱将不可用。可以删掉本组件目录后重新安装。");
            else if (status == "OK")
                log?.Invoke("显卡兼容：" + detail);
        }
        Report(73, "PyTorch 就绪");

        // ---------- ⑤ 侧车脚本 ----------
        Report(73, "写入转谱脚本");
        ExtractResource("AudioToScore.transcribe.py", Path.Combine(root, AudioToScore.ScriptRelative));
        ExtractResource("AudioToScore.fetch_models.py", Path.Combine(root, "fetch_models.py"));

        // ---------- ⑥ 模型权重 ----------
        string modelsDir = Path.Combine(root, "models");
        if (File.Exists(Path.Combine(modelsDir, "SheetSage2", "model.safetensors"))
            && File.Exists(Path.Combine(modelsDir, "MERT-v2-FullSong", "model.safetensors")))
        {
            Report(98, "模型权重已存在，跳过下载");
        }
        else
        {
            Report(74, $"下载模型权重（{Human(ModelsBytes)}）");
            var state = new PipState { Total = ModelsBytes, Caption = "下载模型权重" };
            int code = await RunAsync(python,
                new[] { Path.Combine(root, "fetch_models.py"), "--dest", modelsDir },
                root, Piped(74, 98, state), token);
            if (code != 0)
                throw new InvalidOperationException(
                    $"下载模型权重失败（退出码 {code}）。可以重跑安装，已下好的文件会跳过。");
        }

        // ---------- ⑦ 自检 ----------
        Report(98, "环境自检");
        var check = await RunCaptureAsync(python,
            new[] { Path.Combine(root, AudioToScore.ScriptRelative), "--check", "--models-root", modelsDir },
            root, token);
        if (check.Code != 0)
            throw new InvalidOperationException("自检未通过：\n" + Tail(check.Output, 12));

        string reason = AudioToScore.Describe(root);
        if (reason.Length > 0) throw new InvalidOperationException("装完仍不可用：" + reason);

        Report(100, "安装完成");
        log?.Invoke("安装完成，环境目录：" + root);
    }

    /// 子进程进度的累计状态。每段安装各自持有一个，互不干扰。
    private sealed class PipState
    {
        /// 这一阶段的预估总字节。
        public long Total;
        /// 已下完的文件字节之和。
        public long Done;
        /// 当前正在下的文件大小。
        public long Current;
        /// 最近一次的文字说明，进度刷新时沿用它。
        public string Caption = "";
    }

    /// 从一行输出里找进度线索。
    /// pip：`Downloading <名> (<大小>)` 给当前文件总量，`Progress <已下> of <总量>` 给当前文件进度。
    /// 侧车：`PROGRESS <0-100> <说明>` 是自家协议，直接可用。
    public static bool TryParseProgress(string line, int from, int to, long total, long done, long current,
        out int percent, out string? caption, out long newDone, out long newCurrent)
    {
        percent = -1;
        caption = null;
        newDone = done;
        newCurrent = current;
        string text = line.Trim();
        if (text.Length == 0) return false;

        if (text.StartsWith("PROGRESS ", StringComparison.Ordinal))
        {
            string rest = text["PROGRESS ".Length..];
            int space = rest.IndexOf(' ');
            string number = space < 0 ? rest : rest[..space];
            caption = space < 0 ? "" : rest[(space + 1)..].Trim();
            if (!int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int inner)) return false;
            percent = from + (int)((to - from) * Math.Clamp(inner, 0, 100) / 100.0);
            return true;
        }

        if (text.StartsWith("Progress ", StringComparison.Ordinal))
        {
            string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4 && parts[2] == "of"
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long fileDone)
                && long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long fileTotal)
                && fileTotal > 0)
            {
                long finished = done + fileDone;
                long estimate = total > 0 ? total : Math.Max((long)(finished / 0.75), 1);
                percent = from + (int)((to - from) * Math.Clamp(finished / (double)estimate, 0, 1));
                return true;
            }
            return false;
        }

        if (text.StartsWith("Downloading ", StringComparison.Ordinal))
        {
            // 形如 "Downloading chardet-7.6.0-cp311-cp311-win_amd64.whl (1.2 MB)"
            // 注意：索引要基于 name 来算——open 若是 text 里的位置，
            // 扣掉 "Downloading " 前缀后可能越出 name 的长度。
            string name = text["Downloading ".Length..];
            int open = name.LastIndexOf('(');
            if (open > 0 && name.EndsWith(")", StringComparison.Ordinal)
                && TryParseSize(name[(open + 1)..^1], out long size))
            {
                newDone = done + current;   // 上一个文件下完了
                newCurrent = size;
                caption = name[..open].Trim() + "（" + Human(size) + "）";
                percent = from + (int)((to - from) * Math.Clamp(newDone / (double)Math.Max(total, 1), 0, 1));
            }
            else
            {
                caption = name.Trim();
            }
            return true;
        }

        if (text.StartsWith("==", StringComparison.Ordinal)
            || text.StartsWith("校验", StringComparison.Ordinal))
        {
            caption = text;
            return true;
        }

        return false;
    }

    private static bool TryParseProgress(string text, PipState state, int from, int to,
        out int percent, out string? caption)
    {
        bool hit = TryParseProgress(text, from, to, state.Total, state.Done, state.Current,
            out percent, out caption, out long newDone, out long newCurrent);
        state.Done = newDone;
        state.Current = newCurrent;
        return hit;
    }

    /// "1.2 MB" / "218.1 MB" / "2.4 GB" → 字节。
    public static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        string[] parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return false;
        double scale = parts[1].ToUpperInvariant() switch
        {
            "B" => 1,
            "KB" => 1024,
            "MB" => 1024 * 1024,
            "GB" => 1024.0 * 1024 * 1024,
            _ => 0,
        };
        if (scale <= 0) return false;
        bytes = (long)(value * scale);
        return true;
    }

    // ---------- 底层：下载与子进程 ----------

    /// 依次尝试多个地址，第一个成功的生效。先写 .part，下完再改名——
    /// 中途断掉留下的是 .part，下次不会把半截文件当成装好的。
    public static async Task DownloadAsync(
        HttpClient http, IReadOnlyList<string> urls, string target,
        Action<long, long>? onProgress, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
        string partial = target + ".part";
        Exception? last = null;

        foreach (string url in urls)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0;
                long done = 0;

                await using (var source = await response.Content.ReadAsStreamAsync(token))
                await using (var sink = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
                {
                    byte[] buffer = new byte[1 << 20];
                    int read;
                    while ((read = await source.ReadAsync(buffer, token)) > 0)
                    {
                        await sink.WriteAsync(buffer.AsMemory(0, read), token);
                        done += read;
                        onProgress?.Invoke(done, total);
                    }
                }

                if (File.Exists(target)) File.Delete(target);
                File.Move(partial, target);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // 换下一个源。下载地址失效比安装失败常见得多，多存几个备用更实在。
                last = e;
            }
        }

        throw new InvalidOperationException(
            $"下载失败：{Path.GetFileName(target)}\n  试过的地址：\n    {string.Join("\n    ", urls)}\n  最后错误：{last?.Message}");
    }

    /// 跑子进程，输出逐行回调，返回退出码。
    public static async Task<int> RunAsync(
        string exe, IReadOnlyList<string> args, string workingDirectory,
        Action<string>? onLine, CancellationToken token)
    {
        using var process = StartProcess(exe, args, workingDirectory);
        process.OutputDataReceived += (_, e) => { if (e.Data is { } line) onLine?.Invoke(line); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) onLine?.Invoke(line); };
        if (!process.Start()) throw new InvalidOperationException("无法启动：" + exe);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* 可能已退出 */ }
            throw;
        }
        // 异步事件可能还没投递完，再等一次同步版把管道收干净，否则最后几行会丢。
        process.WaitForExit();
        return process.ExitCode;
    }

    /// 跑子进程并把输出收全（自检用）。
    public static async Task<(int Code, string Output)> RunCaptureAsync(
        string exe, IReadOnlyList<string> args, string workingDirectory, CancellationToken token)
    {
        using var process = StartProcess(exe, args, workingDirectory);
        if (!process.Start()) throw new InvalidOperationException("无法启动：" + exe);
        var output = new StringBuilder();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        output.Append(await stdout).Append(await stderr);
        process.WaitForExit();
        return (process.ExitCode, output.ToString());
    }

    /// 强制换装另一套 torch（跨 CUDA 版本或降级 CPU 时用），装完重新冒烟测试。
    /// 返回重新探针后的状态：OK / FAIL / ABSENT（ABSENT 只可能出现在 CPU 版之后，视为 OK）。
    /// 不带 --no-deps：CUDA 运行库（nvidia-* 系列包）必须跟着 torch 一起换，版本是配对的。
    private static async Task<string> ReinstallTorchAsync(
        string python, string root, IReadOnlyList<string> indexes, string label, long bytes,
        Action<string> onLine, Action<string>? log, CancellationToken token)
    {
        foreach (string index in indexes)
        {
            var args = new List<string>
            {
                "-m", "pip", "install", "--no-warn-script-location", "--force-reinstall",
                "torch==2.8.0", "torchaudio==2.8.0",
                "--index-url", index,
                "--extra-index-url", PyPiMirror,
                "--extra-index-url", "https://download.pytorch.org/whl/cpu",
            };
            log?.Invoke($"从 {index} 改装 {label}（{Human(bytes)}）…");
            int code = await RunAsync(python, args, root, onLine, token);
            if (code != 0)
            {
                log?.Invoke($"{label} 安装失败（退出码 {code}），换下一个源。");
                continue;
            }

            var probe = await RunCaptureAsync(python, new[] { "-c", CudaProbeScript }, root, token);
            string probeLine = probe.Output.Split('\n')
                .Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("PROBE CUDA_", StringComparison.Ordinal)) ?? "";
            // CPU 版装好后 CUDA 必然"缺席"，这正是预期结果，算成功。
            if (TryParseCudaProbe(probeLine, out string status, out string detail) && status is "OK" or "ABSENT")
                return "OK";
            log?.Invoke($"{label} 装上后探针仍不通过（{(detail.Length > 0 ? detail : "无探针输出")}）。");
        }
        return "FAIL";
    }

    private static Process StartProcess(string exe, IReadOnlyList<string> args, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in args) startInfo.ArgumentList.Add(argument);
        // 不设这几个的话，重定向后的 Python 会按系统 ANSI 代码页输出，中文全是乱码。
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        // 侧车与自检都不该去联网碰 HF：权重是本地目录给的。
        startInfo.Environment["HF_HUB_OFFLINE"] = "1";
        startInfo.Environment["TRANSFORMERS_OFFLINE"] = "1";
        return new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }

    private static async Task<bool> HasPipAsync(string python, string workingDirectory, CancellationToken token)
    {
        try
        {
            var result = await RunCaptureAsync(python, new[] { "-m", "pip", "--version" }, workingDirectory, token);
            return result.Code == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// 把内嵌的侧车脚本写到目标位置。脚本随 EXE 一起发布，
    /// 用户那边不需要额外的仓库文件。
    public static void ExtractResource(string name, string target)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) throw new InvalidOperationException("程序内部缺少资源：" + name);
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
        using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.CopyTo(file);
    }

    private static double Ratio(long done, long total) =>
        total > 0 ? Math.Clamp(done / (double)total, 0, 1) : 0;

    private static string Percent(long done, long total) =>
        total > 0 ? (Ratio(done, total) * 100).ToString("0") + "%" : "";

    private static string Tail(string text, int lines)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", all.Length <= lines ? all : all[^lines..]).Trim();
    }
}
