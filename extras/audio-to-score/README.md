# 音频转谱（SheetSage2 附加组件）

把一段音频（歌曲、哼唱、口琴录音）转成 MIDI，再由主程序导入成可演奏的简谱。

这是**可选附加组件**，不是软件自带功能。它把开源模型 [SheetSage2](https://huggingface.co/m-a-p/SheetSage2)
和它的父模型 [MERT-v2-FullSong](https://huggingface.co/m-a-p/MERT-v2-FullSong) 装在本机，
主程序只负责起进程、读结果、把 MIDI 接进现有导入管线。

---

## 为什么不放进分发包

| 项目 | 体积 |
| --- | --- |
| SheetSage2 权重 | 218 MB |
| MERT-v2-FullSong 权重 | 2.4 GB |
| PyTorch + 依赖环境 | **6.8 GB**（其中 CUDA 版 torch 占 6.3 GB） |
| **合计** | **约 9.3 GB** |

主程序的分发包只有 163 MB。为了一个可选功能把发布体积撑大 50 多倍并不划算；
而且模型权重是**非商业许可**（CC BY-NC 4.0，见文末），适合由使用者按需自行获取。

所以：**软件只带本体，环境在你自己的机器上一键装出来**——不需要预装 Python，
不需要开命令行，全程在软件界面里完成。

---

## 安装（推荐）：软件内一键安装

在软件主界面点「音频转谱」。检测到环境不存在时，会自动弹出安装向导，
告诉你预计下载量、装到哪里、用 GPU 还是 CPU。点「开始安装」即可：

- **不需要预装 Python**——安装器会下载官方的嵌入式 Python 3.11（约 11 MB），
  放在组件目录里，不碰你系统里已有的任何 Python。
- **自动判断显卡**：检测到 NVIDIA 显卡就装 CUDA 版 PyTorch（下载约 2.9 GB），
  没有就装 CPU 版（约 230 MB，转谱慢很多但能用）。
- **可中断续装**：中途取消不白费——已下好的部分保留着，下次点安装接着来。
- **装完自动识别**：环境装在软件 EXE 旁的 `音频转谱\` 目录，
  正是主程序默认查找的位置，关掉向导就能直接转谱。

装完后的目录结构（嵌入式布局）：

```
音频转谱\
  python.exe / python311.dll / python311.zip   ← 嵌入式 Python（约 30 MB）
  Lib\site-packages\                           ← torch 等依赖（约 6.8 GB）
  transcribe.py / fetch_models.py              ← 侧车脚本（由程序从资源释放）
  models\
    SheetSage2\                                ← 218 MB
    MERT-v2-FullSong\                          ← 2.4 GB
```

**需要什么**：Windows 10 1809+；至少 **15 GB** 空闲磁盘（建议 20 GB）；
首次安装要联网下载约 **5.5 GB**（GPU 版）或 **2.8 GB**（CPU 版）。

> 不需要 FFmpeg。侧车脚本用 `soundfile` 直接解码音频（libsndfile 1.2 起原生支持 MP3），
> 音频格式支持 wav / mp3 / flac / ogg。

---

## 安装（备选）：手动脚本 setup.ps1

**这是给从仓库构建软件的开发者用的**，面向的是愿意自己动手的人。
普通用户请直接用上面的软件内安装。

与应用内安装的区别：

| | 软件内一键安装 | setup.ps1 |
| --- | --- | --- |
| Python | 自带嵌入式 3.11，无需预装 | 用你系统里的 Python **3.10 / 3.11** |
| 布局 | 嵌入式（目录可整体拷到别的 Win64 机器） | `venv\`（**不可移植**，见下文） |
| 默认位置 | EXE 旁的 `音频转谱\` | 仓库父目录的 `AudioToScore-runtime\` |
| 入口 | 软件界面里的向导 | 手动执行 PowerShell |

用法：

```powershell
# 默认位置（仓库父目录下的 AudioToScore-runtime）
powershell -ExecutionPolicy Bypass -File setup.ps1

# 指定安装位置
powershell -ExecutionPolicy Bypass -File setup.ps1 -Root "D:\AudioToScore-runtime"

# 没 N 卡 / 不想装 CUDA 版
powershell -ExecutionPolicy Bypass -File setup.ps1 -CpuOnly

# 先只建环境，权重稍后自己下
powershell -ExecutionPolicy Bypass -File setup.ps1 -SkipModels
```

Python 必须是 **3.10 或 3.11**——模型锁定的 `numpy==1.24.3` 在 3.12+ 上装不了。
脚本会用 `py -3.11` / `py -3.10` 启动器自动找；找不到就用 `-PythonExe` 手动指定。

中途断了直接重跑同一个命令——已下好的文件会跳过。

> **venv 布局不可移植**：`venv\pyvenv.cfg` 里写死了创建机器的 Python 路径，
> venv 自身也不带 `python311.dll`。所以 setup.ps1 装出来的目录**不能拷到别的电脑用**
> （同一台机器上换个盘可以，记得设环境变量指认新位置）。
> 需要"拷走就能用"的话，请用软件内一键安装（嵌入式布局）。

---

## 软件怎么找到它

主程序按**这个顺序**找运行目录，第一个检查通过的就会被使用：

1. 环境变量 `HARMONICA_AUDIO2SCORE` 指认的目录
2. 设置文件里记录的目录（在软件里选过一次就会记住）
3. **EXE 同级**的 `音频转谱\` 文件夹（软件内一键安装的默认位置）
4. **仓库父目录**下的 `AudioToScore-runtime\`（开发布局；`setup.ps1` 的默认位置）
5. `%LOCALAPPDATA%\HarmonicaPlayer\AudioToScore-runtime\`

一个目录要"能用"必须同时具备：

```
<运行目录>\
  python.exe  或  venv\Scripts\python.exe   ← Python 环境（两种布局都认）
  transcribe.py                             ← 侧车脚本
  models\SheetSage2\model.safetensors
  models\MERT-v2-FullSong\model.safetensors
```

找不到时软件会弹出安装向导；选「我已经装好了」可以手动指认目录，
指认对了会记住，下次直接用。

---

## 命令行用法

不通过软件也能直接跑（调试或批量处理用）。
注意 python 的路径随布局不同：

```powershell
cd <运行目录>

# 嵌入式布局（软件内一键安装出来的）
.\python.exe transcribe.py --audio "D:\music\song.mp3" --out "D:\out\song"

# venv 布局（setup.ps1 装出来的）
.\venv\Scripts\python.exe transcribe.py --audio "D:\music\song.mp3" --out "D:\out\song"
```

```powershell
# 环境自检（打印 torch 版本 / 显卡 / 权重状态，JSON 输出）
.\python.exe transcribe.py --check

# 只转前 60 秒，快速看效果
.\python.exe transcribe.py --audio song.mp3 --out out\ --max-seconds 60

# 强制 CPU / 指定主旋律来源
.\python.exe transcribe.py --audio song.mp3 --out out\ --device cpu
.\python.exe transcribe.py --audio song.mp3 --out out\ --melody-track instrumental
```

| 参数 | 说明 |
| --- | --- |
| `--audio` | 输入音频：wav / mp3 / flac / ogg |
| `--out` | 输出目录（不存在会自动创建） |
| `--models-root` | 放着 `SheetSage2\` 与 `MERT-v2-FullSong\` 的目录，默认脚本同级的 `models\` |
| `--device` | `auto`（默认）/ `cuda` / `cpu` |
| `--dtype` | `auto`（默认，显卡上 bf16、CPU 上 fp32）/ `bf16` / `fp32` |
| `--max-seconds` | 只转前 N 秒，用来快速试听 |
| `--melody-track` | 选哪一路当主旋律：`auto`（谁音符多用谁）/ `vocal` / `instrumental` / `melody` |
| `--check` | 只打印环境信息后退出 |

### 输出文件

```
<out>\
  melody.mid      ← 主旋律 MIDI，主程序导入的就是它
  score.abc       ← 模型给出的 ABC 记谱（有就写）
  summary.json    ← 本次转谱的摘要（时长、耗时、设备、显存峰值、音符数…）
  result.json     ← 模型自己的原始产物，不覆盖
```

主程序读的是 `summary.json`。关键字段：

| 字段 | 含义 |
| --- | --- |
| `melody_file` | 产出的 MIDI 绝对路径 |
| `duration_sec` / `elapsed_sec` | 音频时长 / 实际耗时 |
| `device` / `device_name` | 跑了哪个设备、卡的名字 |
| `dtype` | bf16 / fp32 |
| `peak_vram_mib` | 显存峰值（MiB），CPU 上为 0 |
| `melody_notes` / `vocal_notes` / `instrumental_notes` | 各路音符数 |
| `melody_track` | 实际选了哪一路当主旋律 |
| `warnings` | 模型给的告警（例如某段没解出音符） |

---

## 实测性能

> 下面是在**开发机**上的实测数据，仅供参考；你的机器会不一样。
> 开发机：i9-13900HX / RTX 4060 Laptop（8188 MiB 显存）/ 16 GB 内存 / Windows 10 22H2。

一次转谱分两段耗时：**加载模型**（固定开销）和**推理**（随音频长度增长）。

### 速度与显存

跑在 **RTX 4060 Laptop（8 GB 显存）+ bf16** 上，三档音频实测：

| 音频时长 | 加载模型 | 推理 | 合计 | 倍速 | 显存峰值 |
| --- | --- | --- | --- | --- | --- |
| 3 秒（口琴短句） | 7.0 s | ≈5 s | ≈12 s | — | 2090 MiB |
| 60 秒 | 7.0 s | 8.7 s | **15.7 s** | **3.8 ×** | 2080 MiB |
| 6 分 13 秒 | 7.0 s | 36.5 s | **43.5 s** | **8.6 ×** | 2090 MiB |

**怎么读这组数**：

- 加载模型是每次调用都要付的固定成本（约 7 秒），所以**音频越长越划算**——6 分钟的歌 43 秒转完，
  3 秒的片段反而要 12 秒。
- **显存只要约 2.1 GB**。4 GB 显存的卡就够，8 GB 有大量余量；峰值与音频长度几乎无关
  （模型按 5 分钟窗口处理，不会因为音频长就吃更多显存）。
- CPU 也能跑，但会慢一个数量级，适合只转几十秒的片段。
- 磁盘占用：环境 + 权重合计约 **9.3 GB**（site-packages 6.8 GB，其中 CUDA 版 torch 6.3 GB；
  两个模型 2.6 GB）。

### 准确性

拿软件自带的 `AudioExamples/phrase-v0.3.3-bpm120.wav` 做验证——它的谱面是已知的：

```
【3】 【2_】 【1】 【2_】 | 【3_.】 【4__】 【3_】 【2.】     （高音3 2 1 2 / 高音3 4 3 2）
```

结果是 **10 个音、音高序列完全正确**（E D D C D D E F E D），
只有两个半拍音被切成了两个更短的音（节奏细节，手工合一即可）。

> ⚠️ **一个已知现象：口琴音色可能整体高一个八度。**
> 上面这段音频实测基频是 E5（659 Hz），模型却输出 E6——正好 +12 个半音。
> 推测是模型把簧片的第二泛音听成了基频；同一次测试里，普通流行器乐（音域 C4–E5）
> 没有这个偏移，说明它与音色相关。
>
> **不需要手工修**：主程序的 MIDI 导入本来就会把整首移八度以适配口琴音域
> （可吹范围 MIDI 48–84，即低音1～高音7 加最高do），并在提示条里写明
> 「整首已降低 1 个八度以适配口琴音域」。实测这三个样本导入后**越界音符都是 0 个**。

**结论**：转谱给的是**起点**，不是终点。音高与旋律走向基本可用，节奏细节
（切分、装饰音）和复杂编曲的分离度还需人工过一遍——好在软件本身就有简谱编辑功能。

---

## 常见问题

**Q：点了「音频转谱」弹出安装向导，是必须装吗？**
是。这个功能需要约 9 GB 的运行环境与模型，不随软件本体提供。
不想装就点「以后再说」，不影响软件其它任何功能。

**Q：安装中途失败 / 断网了？**
重新点一次「音频转谱」→「开始安装」即可。已下好的部分（Python 环境、依赖、
模型文件）都会保留并跳过，只会接着下缺的部分。

**Q：提示找不到环境？**
说明环境没装或装到了别的地方。点「音频转谱」让安装向导跑一次；
或者你已经装过了，就在向导里点「我已经装好了」手动指认目录，
也可以设环境变量 `HARMONICA_AUDIO2SCORE=<运行目录>`。

**Q：下载模型权重很慢？**
权重走 **ModelScope** 镜像（实测 24 MB/s）。命令行手动下载时可加 `--source hf`
改走 hf-mirror 备选，但它对 `/resolve/` 有限流，大文件几乎下不动，不建议。

**Q：转出来的简谱音不对 / 少了音？**
模型输出的是"主旋律"，对纯人声、单声道乐器（比如口琴独奏）效果最好；
编曲复杂、鼓点重的流行歌容易把打击乐也解成音符。可以：
- 先剪出副歌/主歌片段再转，短片段质量更高；
- 试 `--melody-track vocal` 或 `instrumental` 手动指定；
- 转出来后在软件里用简谱编辑功能手工修（转谱给的是起点，不是终点）。

**Q：能转多长的音频？**
单窗口 5 分钟，超了自动分段。超长音频（比如整张专辑）会线性变久，建议先切片。

**Q：显存不够？**
实测峰值只要约 **2.1 GB**，而且与音频长度几乎无关，所以 4 GB 显存的卡就够用。
显存实在紧张就 `--device cpu`（慢很多但能跑）。显存峰值记录在 `summary.json` 的
`peak_vram_mib` 里，可以据此判断自己的机器够不够。

**Q：能把这个组件拷到别的电脑上用吗？**
看你用哪种方式装的：
- **软件内一键安装**（嵌入式布局）：可以。整个 `音频转谱\` 文件夹拷过去，
  放到目标机器软件 EXE 旁即可，不依赖目标机器装没装 Python。
- **setup.ps1**（venv 布局）：不行。venv 写死了创建机器的 Python 路径，
  只能在原机器上用（同机换盘可以，设环境变量指认新位置）。

**Q：删掉这个组件会影响主程序吗？**
不会。主程序里「音频转谱」按钮找不到环境只会弹安装向导，其它功能完全不受影响。
想彻底卸载，直接删掉整个组件目录即可。

---

## 许可与署名

本文件夹里的脚本（`setup.ps1` / `transcribe.py` / `fetch_models.py`）随主仓库一起发布。

**模型权重是第三方产物，许可是非商业的，必须遵守：**

- **SheetSage2** 与 **MERT-v2-FullSong** 权重：**CC BY-NC 4.0**
  （Creative Commons Attribution-NonCommercial 4.0 International）
  → **只可用于非商业用途**。商业使用需另行联系模型作者取得授权。
- 出处：<https://huggingface.co/m-a-p/SheetSage2> · <https://huggingface.co/m-a-p/MERT-v2-FullSong>
- 权重下载自 ModelScope 的 `m-a-p` 组织镜像（与 huggingface 上的同名仓库内容一致）。
- 其余第三方依赖（Transformers / PyTorch / abcjs / FluidR3 等）各自的许可见模型目录里的
  `THIRD_PARTY_NOTICES.md`，仓库根目录的 `THIRD-PARTY-NOTICES.md` 也一并列出。

引用模型时请注明：

> SheetSage2, MERT-v2-FullSong — m-a-p (Multimodal Art Projection), CC BY-NC 4.0.

---

## 目录结构

```
extras/audio-to-score/           ← 本文件夹（随仓库发布，体积很小）
  README.md                      ← 你正在看的这份
  setup.ps1                      ← 手动安装脚本（开发者备选）
  transcribe.py                  ← 侧车脚本：音频 → MIDI（也内嵌在主程序 EXE 里）
  fetch_models.py                ← 权重下载（也内嵌在主程序 EXE 里）

<软件 EXE 旁>\音频转谱\           ← 软件内一键安装出来的目录（嵌入式布局，可移植）
  python.exe / python311.dll …   ← 嵌入式 Python
  Lib\site-packages\             ← 依赖（约 6.8 GB）
  models\SheetSage2\             ← 218 MB
  models\MERT-v2-FullSong\       ← 2.4 GB
  out\                           ← 每次转谱的输出（melody.mid / summary.json …）

<仓库父目录>\AudioToScore-runtime\   ← setup.ps1 装出来的目录（venv 布局，不可移植）
  venv\                          ← Python 环境（6.8 GB）
  models\                        ← 同上
```
