#!/usr/bin/env python3
"""下载 SheetSage2 与它的父模型 MERT-v2-FullSong 到本地目录。

为什么不用 huggingface-cli：
  1) huggingface.co 在国内直连不通，hf-mirror.com 对 /resolve/ 有配额限制，
     142 个文件并发拉会直接 429；而且 2.4GB 的大文件实测只有几十 B/s。
  2) ModelScope 上有同名的完整镜像，实测 24 MB/s，且支持 range 断点续传。
  3) SheetSage2 的 from_pretrained 支持 base_model_path 参数，可以直接吃本地目录，
     完全不需要 HF 缓存，整个附加组件可以整体拷贝/删除。

用法：
    python fetch_models.py --dest "D:/洲口琴工具/AudioToScore-runtime/models"
    python fetch_models.py --dest ... --source hf      # 走 hf-mirror 备选
    python fetch_models.py --dest ... --only SheetSage2
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

# 父模型权重必须逐字节匹配，SheetSage2 加载时会校验这个 sha256。
MERT_SHA256 = "e6dd2ab187d6dd62b6521cd7d8f932e237acf0c5757745a7232082e28391350d"
MERT_REVISION = "d8ba1c745e733b3908ce6ad16ebeb17ac7600a42"

REPOS = ["SheetSage2", "MERT-v2-FullSong"]

SOURCES = {
    # 实测：ModelScope 快得多，作为默认来源。
    "modelscope": {
        "files": "https://www.modelscope.cn/api/v1/models/m-a-p/{repo}/repo/files?Revision=master&Recursive=True",
        "file": "https://www.modelscope.cn/api/v1/models/m-a-p/{repo}/repo?Revision=master&FilePath={path}",
        "revision": "master",
    },
    "hf": {
        "files": "https://hf-mirror.com/api/models/m-a-p/{repo}?blobs=true",
        "file": "https://hf-mirror.com/m-a-p/{repo}/raw/{rev}/{path}",
        "revision": None,  # SheetSage2 用 main，MERT 用固定 revision
    },
}

# 只下文件，不下目录。ModelScope 的文件列表里目录项的 Path 不带尾部斜杠
# （assets、benchmarks、render_assets、tests），按前缀过滤会漏掉它们、直接 404。
# 两边仓库去掉目录项后合计约 5MB 的杂项，一律照单全收，避免漏文件。


def human(n: float) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return f"{n:.1f} {unit}"
        n /= 1024
    return f"{n:.1f} GB"


def request(url: str, *, retries: int = 4, timeout: int = 30):
    last = None
    for attempt in range(retries):
        try:
            return urllib.request.urlopen(url, timeout=timeout)
        except (urllib.error.URLError, OSError) as exc:  # 含超时与 DNS 失败
            last = exc
            time.sleep(2 * (attempt + 1))
    raise SystemExit(f"请求失败：{url}\n  {last}")


def list_files(repo: str, source: str) -> list[tuple[str, int]]:
    spec = SOURCES[source]
    url = spec["files"].format(repo=repo)
    with request(url) as response:
        payload = json.load(response)
    if source == "modelscope":
        entries = [e for e in payload["Data"]["Files"] if e.get("Type") in (None, "blob")]
        pairs = [(e["Path"], int(e.get("Size") or 0)) for e in entries]
    else:
        pairs = [(e["rfilename"], int(e.get("size") or 0)) for e in payload.get("siblings", [])]
    return pairs


def file_url(repo: str, source: str, path: str) -> str:
    spec = SOURCES[source]
    revision = spec["revision"]
    if source == "hf" and repo == "MERT-v2-FullSong":
        revision = MERT_REVISION
    elif source == "hf":
        revision = "main"
    return spec["file"].format(repo=repo, path=urllib.parse.quote(path), rev=revision)


def sha256_of(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def download(repo: str, path: str, size: int, dest_root: str, source: str) -> bool:
    target = os.path.join(dest_root, repo, path.replace("/", os.sep))
    if os.path.exists(target) and (size == 0 or os.path.getsize(target) == size):
        print(f"  跳过（已存在） {path}")
        return False
    os.makedirs(os.path.dirname(target), exist_ok=True)
    url = file_url(repo, source, path)
    partial = target + ".part"
    done = os.path.getsize(partial) if os.path.exists(partial) else 0
    headers = {"Range": f"bytes={done}-"} if done else {}
    req = urllib.request.Request(url, headers=headers)
    with request(req, timeout=60) as response:
        if done and response.status != 206:  # 服务端不支持续传，从头来
            done = 0
        mode = "ab" if done else "wb"
        started, ticks = time.time(), 0
        with open(partial, mode) as handle:
            while True:
                block = response.read(1 << 20)
                if not block:
                    break
                handle.write(block)
                done += len(block)
                ticks += 1
                if ticks % 8 == 0 and size:
                    speed = done / max(time.time() - started, 1e-6)
                    print(f"\r    {path}  {done / size * 100:5.1f}%  {human(speed)}/s   ", end="")
    if size and os.path.getsize(partial) != size:
        raise SystemExit(f"大小不符：{path} 期望 {size} 实际 {os.path.getsize(partial)}")
    os.replace(partial, target)
    print(f"\r    {path}  {human(os.path.getsize(target))} 完成            ")
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description="下载 SheetSage2 及其父模型")
    parser.add_argument("--dest", required=True, help="模型存放根目录（下面会建 SheetSage2/ 与 MERT-v2-FullSong/）")
    parser.add_argument("--source", choices=sorted(SOURCES), default="modelscope")
    parser.add_argument("--only", choices=REPOS, help="只下其中一个")
    args = parser.parse_args()

    repos = [args.only] if args.only else REPOS
    for repo in repos:
        print(f"== {repo}（来源 {args.source}）")
        files = list_files(repo, args.source)
        total = sum(size for _, size in files)
        print(f"   {len(files)} 个文件，合计 {human(total)}")
        for path, size in sorted(files, key=lambda x: x[1]):  # 先小后大，早点拿到代码
            download(repo, path, size, args.dest, args.source)

    weights = os.path.join(args.dest, "MERT-v2-FullSong", "model.safetensors")
    if os.path.exists(weights):
        print("== 校验 MERT-v2 权重 sha256 …")
        actual = sha256_of(weights)
        if actual != MERT_SHA256:
            raise SystemExit(f"校验失败：\n  期望 {MERT_SHA256}\n  实际 {actual}")
        print("   校验通过")
    print("完成。模型目录：" + os.path.abspath(args.dest))
    return 0


if __name__ == "__main__":
    sys.exit(main())
