本目录用于放置 JSON 曲谱（软件里“导入 JSON”或左侧曲库都会读取）。

本仓库未附带 GTIartist 曲库的曲谱数据（那属于第三方商业软件的曲库），
请自行准备 JSON 文件放入本目录，例如：

- GTIartist 曲谱：format 为 gtiartist-score-v1，notes 里为 MIDI 音高与秒数
- 兼容格式：任何含 notes 数组（pitch / start / duration）的 JSON 对象

放入后启动软件即可在左侧曲库看到；多音轨曲谱会弹窗让你选择演奏哪一条音轨。
