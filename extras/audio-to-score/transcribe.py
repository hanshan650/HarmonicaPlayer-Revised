#!/usr/bin/env python3
"""把音频转成简谱可用的 MIDI（SheetSage2 侧车进程）。

这个脚本是「附加组件」的一部分：模型、Python 环境和本文件都**不进分发包**，
用户自己装好后由主程序调用。主程序只做两件事——起进程、读结果。

与官方 infer.py 的差别只有两处，都是为了能离线跑：
  1) 传 base_model_path 指向本地的 MERT-v2-FullSong，不去 huggingface.co 拉父模型。
     （这是 SheetSage2 官方支持的参数，源码里 base_path = kwargs.pop("base_model_path")）
  2) 先用 soundfile 把音频解成波形再喂给模型，因此不需要装 FFmpeg。
     官方路径走 ffmpeg CLI 解文件，本机没装，且 Windows 上多一层依赖很容易踩坑。

给主程序看的输出协议（stdout，逐行）：
    PROGRESS <0-100> <说明>
    DONE <result.json 的绝对路径>
    ERROR <原因>
其余日志一律走 stderr，避免污染协议。

用法：
    python transcribe.py --audio song.mp3 --out out/ --models-root models/
    python transcribe.py --check                 # 只打印环境信息，给界面判断能不能用
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from pathlib import Path

SAMPLE_RATE = 24000


def emit(line: str) -> None:
    print(line, flush=True)


def note(percent: int, text: str) -> None:
    emit(f"PROGRESS {max(0, min(100, int(percent)))} {text}")


def quiet_logs(home: Path | None = None) -> None:
    # 必须在 import transformers 之前设，否则会先去碰网络。
    if home is not None:
        # trust_remote_code 会把 SheetSage2 的 .py 复制到 transformers 的动态模块缓存里。
        # 默认位置在 %USERPROFILE%\.cache\huggingface，会让「整个组件可整体拷贝」的说法失效；
        # 这里改指组件目录内的 hf\，装完就能连文件夹一起搬走。
        try:
            modules = home.resolve() / "modules"
            modules.mkdir(parents=True, exist_ok=True)
            os.environ["HF_MODULES_CACHE"] = str(modules)
        except OSError:
            pass  # 目录建不出来就退回默认位置，不影响功能
    os.environ.setdefault("HF_HUB_DISABLE_PROGRESS_BARS", "1")
    os.environ.setdefault("HF_HUB_DISABLE_TELEMETRY", "1")
    os.environ.setdefault("HF_HUB_OFFLINE", "1")
    os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")
    os.environ.setdefault("TOKENIZERS_PARALLELISM", "false")


def cuda_kernel_probe() -> tuple[bool, str]:
    """真发射一次 CUDA 内核。返回 (是否可用, 详情)。

    torch.cuda.is_available() 只说明驱动能初始化 CUDA，不代表这套 torch
    编译了这张卡的内核——典型反例：RTX 50 系（Blackwell）配 cu126 版 torch，
    初始化一切正常，要到真正发射内核才报
    "no kernel image is available for execution on the device"。
    所以"能不能用"必须以试点运算为准。
    """
    import torch

    if not torch.cuda.is_available():
        return False, "CUDA 不可用"
    try:
        probe = torch.randn(128, 128, device="cuda", dtype=torch.bfloat16)
        (probe @ probe.mT).float().sum().item()
        return True, torch.cuda.get_device_name(0)
    except Exception as exc:
        text = str(exc) or type(exc).__name__
        return False, text.splitlines()[0]


def describe_environment(models_root: Path, device: str) -> dict:
    info: dict = {"models_root": str(models_root), "python": sys.version.split()[0]}
    try:
        import torch
        info["torch"] = torch.__version__
        info["cuda_available"] = torch.cuda.is_available()
        if torch.cuda.is_available():
            info["device_name"] = torch.cuda.get_device_name(0)
            info["vram_total_mib"] = round(torch.cuda.get_device_properties(0).total_memory / 1024**2)
        info["device"] = "cuda" if (device == "auto" and torch.cuda.is_available()) else (
            "cpu" if device == "auto" else device)
        works, detail = cuda_kernel_probe()
        info["cuda_works"] = works
        if not works:
            info["cuda_note"] = detail
    except Exception as exc:  # torch 没装好时也要能报出来
        info["torch_error"] = str(exc)

    # 光看文件在不在不够：模型仓库的 requirements 漏装一个包，要到加载模型时才炸。
    # 这里把真正会被 import 的依赖逐个试一遍（只 import 顶层，不触发模型的动态模块）。
    missing: list[str] = []
    for module in ("torch", "torchaudio", "transformers", "numpy", "scipy",
                   "mir_eval", "soundfile", "pretty_midi", "mido"):
        try:
            __import__(module)
        except Exception as exc:
            missing.append(f"{module}（{type(exc).__name__}）")
    info["missing_modules"] = missing

    ss2, mert = models_root / "SheetSage2", models_root / "MERT-v2-FullSong"
    info["sheetsage2_dir"] = str(ss2)
    info["mert_dir"] = str(mert)
    info["sheetsage2_config"] = (ss2 / "config.json").is_file()
    info["sheetsage2_weights"] = (ss2 / "model.safetensors").is_file()
    info["mert_weights"] = (mert / "model.safetensors").is_file()
    info["mert_code"] = (mert / "modeling_mert2.py").is_file()

    def size_mb(path: Path) -> float:
        return round(path.stat().st_size / 1048576, 1) if path.is_file() else 0.0

    info["sheetsage2_weights_mb"] = size_mb(ss2 / "model.safetensors")
    info["mert_weights_mb"] = size_mb(mert / "model.safetensors")
    info["ready"] = all(info[key] for key in
                        ("sheetsage2_config", "sheetsage2_weights", "mert_weights", "mert_code")) \
        and "torch_error" not in info and not missing
    return info


def decode_audio(path: Path, max_seconds: float | None):
    """用 soundfile 解码成 [channels, samples] 的 float32 波形。

    这样就不需要 FFmpeg：模型收到数组时只走 torchaudio 的重采样，纯 torch 实现。
    libsndfile 1.2 起原生支持 MP3，wav/flac/ogg 更没问题。
    """
    import numpy as np
    import soundfile as sf

    if max_seconds is not None:
        frames = int(max_seconds * 48000) + 48000  # 先按常见采样率估算，读进来再截
    else:
        frames = -1
    data, rate = sf.read(str(path), dtype="float32", always_2d=True, frames=frames)
    if data.size == 0:
        raise ValueError("音频是空的")
    waveform = data[:, :4].T  # [channels, samples]，最多取 4 声道
    if max_seconds is not None:
        waveform = waveform[:, : int(max_seconds * rate)]
    return np.ascontiguousarray(waveform, dtype="float32"), int(rate)


def transcribe(args: argparse.Namespace) -> int:
    models_root = Path(args.models_root).resolve()
    ss2_dir, mert_dir = models_root / "SheetSage2", models_root / "MERT-v2-FullSong"
    audio_path = Path(args.audio).resolve()
    out_dir = Path(args.out).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)

    if not audio_path.is_file():
        emit(f"ERROR 找不到音频文件：{audio_path}")
        return 2
    for required in (ss2_dir / "config.json", ss2_dir / "model.safetensors", mert_dir / "model.safetensors"):
        if not required.is_file():
            emit(f"ERROR 模型文件缺失：{required}")
            return 3

    note(2, "准备环境")
    quiet_logs(Path(__file__).resolve().parent / "hf")
    import torch
    from transformers import AutoModel

    device = args.device
    if device == "auto":
        device = "cuda" if torch.cuda.is_available() else "cpu"
    if device == "cuda" and not torch.cuda.is_available():
        device = "cpu"
    dtype_name = args.dtype
    if dtype_name == "auto":
        dtype_name = "bf16" if device == "cuda" else "fp32"

    if device == "cuda":
        works, detail = cuda_kernel_probe()
        if not works:
            # 已装的 torch 没有这张卡的内核（如 RTX 50 系 + cu126 版）。
            # 趁早降级 CPU，总比在模型加载到一半时炸掉强。
            emit(f"注意：当前 PyTorch 与显卡不兼容（{detail}），本次改用 CPU 转谱，"
                 "速度会慢很多。重装附加组件（删除后重装点「音频转谱」）可自动换装匹配的版本。")
            device = "cpu"
            if args.dtype == "auto":
                dtype_name = "fp32"

    note(6, "加载模型（首次约 10 秒）")
    started = time.monotonic()
    model = AutoModel.from_pretrained(
        str(ss2_dir),
        trust_remote_code=True,
        # 父模型走本地目录：不去 huggingface.co，也不需要 HF 缓存。
        base_model_path=str(mert_dir),
        torch_dtype=torch.bfloat16 if dtype_name == "bf16" else torch.float32,
        device_map={"": device},
    ).eval()
    load_seconds = time.monotonic() - started

    note(20, "解码音频")
    waveform, rate = decode_audio(audio_path, args.max_seconds)
    duration = waveform.shape[1] / rate
    note(24, f"音频 {duration:.1f} 秒 / {rate} Hz")

    stages = {"encoding": "编码", "decoding": "解码音符", "window_complete": "窗口完成"}
    last_percent = 24
    last_emitted: tuple[int, str] = (0, "")

    def on_progress(event: dict) -> None:
        nonlocal last_percent, last_emitted
        stage = str(event.get("stage", ""))
        index, total = event.get("window"), event.get("windows")
        if not isinstance(index, int) or not isinstance(total, int) or total <= 0:
            return
        percent = 24 + int(72 * (index - (0.5 if stage == "encoding" else 0)) / total)
        percent = max(last_percent, min(96, percent))
        last_percent = percent
        text = f"第 {index}/{total} 段：{stages.get(stage, stage)}"
        # 解码是逐 token 循环，同一阶段会回调上千次；完全相同的一行只报一次，
        # 否则界面日志会被「96% 解码音符」刷屏。
        if (percent, text) == last_emitted:
            return
        last_emitted = (percent, text)
        note(percent, text)

    note(25, "开始推理")
    result = model.transcribe(
        waveform,
        sampling_rate=rate,
        output_dir=str(out_dir),
        dtype=dtype_name,
        max_seconds=args.max_seconds,
        progress=on_progress,
    )
    elapsed = time.monotonic() - started

    note(97, "整理结果")
    midis = result.get("midis") or {}
    vocal = result.get("vocal_notes", 0)
    instrumental = result.get("instrumental_notes", 0)
    track = args.melody_track
    if track == "auto":
        track = "vocal" if vocal >= instrumental else "instrumental"
    chosen = f"melody_{track}"
    if chosen not in midis:
        chosen = "melody"

    melody_file = out_dir / "melody.mid"
    melody_file.write_bytes(midis[chosen])

    abc = result.get("abc")
    if abc:
        abc_file = out_dir / "score.abc"
        abc_file.write_text(abc, encoding="utf-8")

    summary = {
        "ok": True,
        "audio": str(audio_path),
        "out_dir": str(out_dir),
        "duration_sec": round(duration, 2),
        "elapsed_sec": round(elapsed, 2),
        "load_sec": round(load_seconds, 2),
        "realtime_factor": round(duration / elapsed, 2) if elapsed > 0 else None,
        "device": device,
        "device_name": torch.cuda.get_device_name(0) if device == "cuda" else "CPU",
        "dtype": dtype_name,
        "peak_vram_mib": round(result.get("peak_gpu_mib") or 0),
        "melody_notes": result.get("melody_notes", 0),
        "vocal_notes": vocal,
        "instrumental_notes": instrumental,
        # 注意：返回字典里 events 是「事件列表」，num_events 才是个数（payload 展开时会覆盖 events）。
        "events": result.get("num_events", 0),
        "abc_measures": result.get("abc_measures", 0),
        "abc_error": result.get("abc_error"),
        "warnings": (result.get("warnings") or [])[:20],
        "melody_track": track,
        "melody_file": str(melody_file),
        "midi_parts": sorted(midis),
        "models_root": str(models_root),
    }
    # 模型自己也会往输出目录写一个 result.json，那是它的原始产物，
    # 我们不覆盖它，摘要另存为 summary.json。
    summary_path = out_dir / "summary.json"
    summary_path.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    note(100, "完成")
    emit("DONE " + str(summary_path))
    return 0


def main() -> int:
    default_root = Path(__file__).resolve().parent / "models"
    parser = argparse.ArgumentParser(description="音频转谱（SheetSage2 侧车）")
    parser.add_argument("--audio", help="输入音频：wav / mp3 / flac / ogg")
    parser.add_argument("--out", help="输出目录")
    parser.add_argument("--models-root", default=os.environ.get("HARMONICA_AUDIO2SCORE_MODELS", str(default_root)),
                        help="放着 SheetSage2/ 与 MERT-v2-FullSong/ 的目录")
    parser.add_argument("--device", default="auto", choices=("auto", "cuda", "cpu"))
    parser.add_argument("--dtype", default="auto", choices=("auto", "bf16", "fp32"))
    parser.add_argument("--max-seconds", type=float, default=None,
                        help="只转前 N 秒，用来快速试听效果")
    parser.add_argument("--melody-track", default="auto",
                        choices=("auto", "vocal", "instrumental", "melody"))
    parser.add_argument("--check", action="store_true", help="只打印环境信息并退出")
    args = parser.parse_args()

    models_root = Path(args.models_root).resolve()
    if args.check:
        info = describe_environment(models_root, args.device)
        print(json.dumps(info, ensure_ascii=False, indent=2))
        # 非零退出码让 setup.ps1 的自检能真正拦住装坏的环境。
        return 0 if info.get("ready") else 4
    if not args.audio or not args.out:
        parser.error("需要 --audio 与 --out（或用 --check 只查环境）")

    try:
        return transcribe(args)
    except KeyboardInterrupt:
        emit("ERROR 已取消")
        return 130
    except Exception as exc:
        import traceback
        traceback.print_exc(file=sys.stderr)
        emit(f"ERROR {type(exc).__name__}: {exc}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
