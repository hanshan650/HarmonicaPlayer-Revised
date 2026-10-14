# 修改声明 / NOTICE

**This is an unofficial modified fork. It is based on the original project
[lanselanlanxi-wq/HarmonicaPlayer](https://github.com/lanselanlanxi-wq/HarmonicaPlayer) and adds
MIDI/JSON score import, multi-track selection, a score library sidebar and a reworked UI.
See the modification list below. All original code and design credit goes to the original author.**

## 1. 来源

- **原作者仓库**：https://github.com/lanselanlanxi-wq/HarmonicaPlayer
- **修改基线**：上游 `main` 的 `212316e`（版本 **v0.3.3**，2026-09-25）
- **本仓库**：`HarmonicaPlayer-Revised`（本修改版，作者 hanshan650），在上游 v0.3.3 基础上继续开发，当前版本 **v1.3**
- 原作者的最新版本始终以上游仓库为准；本仓库不是官方版本，也不代表原作者的意见。

## 2. 许可与版权

- **上游仓库没有提供 LICENSE 文件**，因此本修改版**不另行声明任何开源许可**，也不改变原代码的版权归属。
- 原代码的著作权属于原作者；本修改版仅由第三方在原作者代码基础上继续修改。
  如需再分发、商用或用于其他项目，请先确认上游仓库的授权情况并取得原作者许可。
- 本修改版包含的音乐曲谱（`简谱\` 下的 TXT/MIDI）是**他人音乐作品的转谱**，版权属于各自原作者/转谱者，
  仅为个人学习与演奏使用，**不随本仓库授权**。
- 本地试听使用的口琴采样来自 VCSL（CC0-1.0），来源、固定提交与处理方式见
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
- 历史贡献来源（PR #1、PR #3 的参考范围）见 [CONTRIBUTIONS.md](CONTRIBUTIONS.md)。

### 本仓库**没有**包含的内容

- **第三方商业软件随附的曲库 JSON**：属于他人作品/数据的转谱，出于版权考虑未纳入本仓库
  （`.gitignore` 已忽略 `简谱/JSON/*.json`）。需要时请自行准备 JSON 曲谱并放入 `简谱\JSON\`，软件即可读取。

## 3. 本项目的修改内容

相对原作者上游版本（v0.3.3，commit `212316e`），本项目做了以下改动。

### 新增功能

- **导入 MIDI**（`MidiImporter.cs`）：读取标准 MIDI（Format 0/1，.mid/.midi）转成单音简谱正文；
  忽略通道 10 打击乐；先整体八度移调，仍超范围的个别音再折回八度；速度取第一个速度事件（20～300）；
  最短音过短时自动把速度降到可演奏值并在提示区说明。
- **导入 JSON**（`JsonImporter.cs`）：读取带 notes 数组的 JSON（pitch 为 MIDI 音高，
  start/duration 为秒；带 bpm 字段时按拍数处理），多音轨可自选一条。
- **共享转换模块**（`ScoreText.cs`）：MIDI 与 JSON 导入共用同一套音域适配、时值量化与简谱输出逻辑。
- **音轨选择与换轨**（`TrackDialog.cs`、`ScoreDialogs.cs`、`Program.cs`）：多音轨曲谱导入时弹窗列出每一条音轨
  （编号、名称、音数、音域、平均音长、同时发声音数），导入后可用“演奏音轨”下拉框随时换轨，不必重新导入文件。
- **曲库侧栏**（`Program.cs`）：启动自动扫描 `简谱\{TXT,MIDI,JSON}`，按曲名列出、选中即导入，
  支持按曲名筛选与一键刷新；手动导入路径保留。
- **界面改版**（`Program.cs`）：左右两栏、功能卡片分区，统一字体与配色，长路径省略号 + 悬停提示，右侧编辑器可滚动。

### 行为修改与问题修复

- **导入后只演奏一条音轨**：不再把所有音轨按“重叠取最高音”合并。此前的合并会让伴奏盖住主旋律，
  是“导入后演奏音符杂乱、音不准”的主因（`JsonImporter.cs`、`MidiImporter.cs`）。
- **不再为个别短音把整首曲子降速**（JSON 导入）：音高与时值不变，改为报告短音数量与建议
  （减小音符间隔 / 建议 BPM），不再阻断试听与演奏；MIDI 导入分支保留“自动降到可演奏速度”的行为
  （`PlaybackValidation.cs`、`JsonImporter.cs`）。
- **时值量化网格由 1/48 拍细化为 1/960 拍**（60 BPM 约 1 ms），低速曲目的拍点误差从十几毫秒降到 1 ms 以内（`ScoreText.cs`）。
- **最小时间网格推断加覆盖率保护**（更细的网格必须仍能解释 ≥95% 的时间值），并修正浮点噪声导致的错误速度推断；
  推断不出时按中性 120 BPM 显示，并说明速度只是标签、演奏仍按原文件的绝对时间对齐（`JsonImporter.cs`）。
- **长音下方后起的旋律音不再被吞掉**：改为“截断长音、保留后起的音”，原实现会丢掉这些音（`ScoreText.cs`）。
- **最短音校验由抛异常改为返回报告**（`PlaybackValidation.CheckGap`：`TooShort`、`FirstShortPosition`、`ShortestMs`）。
- **默认选轨**：JSON 默认编号最小的音轨（第一声部通常就是主旋律）；MIDI 默认音符最多的音轨。

### 工程与文档

- 测试扩充：新增 `Tests/MidiImportTests.cs`、`Tests/JsonImportTests.cs`；
  Windows 冒烟测试新增曲库侧栏、MIDI/JSON 选轨与换轨用例；原有测试按新语义更新。
- 手册（`使用说明.txt`、`制谱说明.txt`、`ai转谱模板.txt`）按主题重新组织，去掉逐版更新段落与过时引用。
- `Build-Release.ps1`：发布自包含单文件 EXE，并把 README、本声明、手册、示例与 `简谱` 文件夹一并打包。
- 本项目自 v1.0 起独立编号（改动基线为上游 v0.3.3），按功能迭代，不另行维护逐版更新日志。

### 未改动的部分

- 上游的按键模拟与游戏演奏流程（`NativeInput.cs`、`NativeHotkeys.cs`、`PlayerWindow.Audio.cs`）、
  TXT 文件头格式（`HarmonicaPlayer/1`）、音高规则、最短音校验（12ms 准备 + 40ms 按住）、
  真实口琴采样音源与短音/尾音处理均沿用上游实现。
- TXT 格式与 `HarmonicaPlayer/1` 无变化，旧谱不需要转换。

## 4. 联系方式

如原作者或任何权利人认为本仓库的内容不妥，请在本仓库提 Issue，我会立即调整或删除相关内容。
也欢迎向上游仓库提交改进，让原作者版本受益。
