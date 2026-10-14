<#
    一键安装「音频转谱」附加组件。

    做的事：建独立 Python 环境 → 装 PyTorch 与依赖 → 把侧车脚本放到运行目录 → 下载模型权重 → 自检。

    为什么不做成分发包的一部分：
      SheetSage2 权重 218 MB + 父模型 MERT-v2-FullSong 2.4 GB，PyTorch 环境再占约 6.8 GB
      （CUDA 版 torch 一家就 6.3 GB），装完合计约 9.3 GB。
      主程序的分发包现在只有 163 MB，塞进去没有意义；
      而且权重许可为非商业的 CC BY-NC 4.0，适合让用户按需自行获取。

    用法：
      powershell -ExecutionPolicy Bypass -File setup.ps1
      powershell -ExecutionPolicy Bypass -File setup.ps1 -Root "D:\洲口琴工具\AudioToScore-runtime"
      powershell -ExecutionPolicy Bypass -File setup.ps1 -SkipModels      # 只建环境，稍后自己下权重
      powershell -ExecutionPolicy Bypass -File setup.ps1 -CpuOnly         # 没 N 卡时装 CPU 版 PyTorch
#>
param(
    [string]$Root,
    [string]$PythonExe,
    [switch]$SkipModels,
    [switch]$CpuOnly,
    [switch]$NoCache
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path (Split-Path $scriptDir -Parent) -Parent
$repoParent = Split-Path $repoRoot -Parent

$PipMirror = 'https://pypi.tuna.tsinghua.edu.cn/simple'
$TorchIndex = 'https://download.pytorch.org/whl/cu126'
$TorchFallbackIndex = 'https://download.pytorch.org/whl/cpu'

function Write-Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Write-Ok([string]$text)   { Write-Host "   $text" -ForegroundColor Green }
function Write-Warn2([string]$text) { Write-Host "   $text" -ForegroundColor Yellow }

# ---------- 1. 决定安装目录 ----------
if (-not $Root) {
    $candidate = Join-Path $repoParent 'AudioToScore-runtime'
    $drive = (Split-Path $candidate -Qualifier)
    $freeGB = (Get-PSDrive -Name $drive.TrimEnd(':')).Free / 1GB
    if ($freeGB -lt 15) {
        throw "默认目录所在磁盘 $drive 只剩 $([math]::Round($freeGB,1)) GB，不够放模型与环境" +
              "（装完约 9.3 GB，装的过程中 pip 缓存与解包还要临时占几个 GB）。" +
              "请用 -Root 指定其它磁盘上的目录，例如 -Root 'D:\AudioToScore-runtime'。"
    }
    $Root = $candidate
}
$Root = [System.IO.Path]::GetFullPath($Root)
Write-Step "安装目录：$Root"

if (-not $NoCache) { $env:PIP_NO_CACHE_DIR = '0' }

# ---------- 2. 找到 Python 3.10 / 3.11 ----------
Write-Step '查找 Python 3.10 或 3.11'
function Test-PythonVersion([string]$exe, [string[]]$prefixArgs) {
    try {
        $code = 'import sys; v=sys.version_info; print("%d.%d" % (v[0], v[1]))'
        $args = @()
        if ($prefixArgs) { $args += $prefixArgs }
        $args += @('-c', $code)
        $version = (& $exe @args 2>$null | Select-Object -First 1)
        if ($LASTEXITCODE -eq 0 -and $version -match '^3\.(10|11)$') { return $version.Trim() }
    } catch { }
    return $null
}

$resolved = $null
if ($PythonExe) {
    $version = Test-PythonVersion $PythonExe @()
    if (-not $version) { throw "$PythonExe 不是可用的 Python 3.10/3.11。" }
    $resolved = @{ Exe = $PythonExe; Args = @(); Version = $version }
} else {
    # py 启动器最可靠，先试它；再试 PATH 上的名字。
    foreach ($minor in '3.11', '3.10') {
        if (Get-Command py -ErrorAction SilentlyContinue) {
            $version = Test-PythonVersion 'py' @("-$minor")
            if ($version) { $resolved = @{ Exe = 'py'; Args = @("-$minor"); Version = $version }; break }
        }
    }
    if (-not $resolved) {
        foreach ($name in 'python3.11', 'python3.10', 'python') {
            if (-not (Get-Command $name -ErrorAction SilentlyContinue)) { continue }
            $version = Test-PythonVersion $name @()
            if ($version) { $resolved = @{ Exe = $name; Args = @(); Version = $version }; break }
        }
    }
}
if (-not $resolved) {
    throw "没找到 Python 3.10 或 3.11。模型锁定的 numpy 1.24.3 在 3.12 以上装不了，" +
          "所以必须是 3.10/3.11。请先安装（python.org 或 winget install Python.Python.3.11）后重试，" +
          "也可以用 -PythonExe 直接指定路径。"
}
Write-Ok "使用 Python $($resolved.Version)：$($resolved.Exe) $($resolved.Args -join ' ')"

# ---------- 3. 建虚拟环境 ----------
$venv = Join-Path $Root 'venv'
$venvPython = Join-Path $venv 'Scripts\python.exe'
if (Test-Path $venvPython) {
    Write-Step '复用已有虚拟环境'
    Write-Ok $venv
} else {
    Write-Step '创建虚拟环境'
    $venvArgs = $resolved.Args + @('-m', 'venv', $venv)
    & $resolved.Exe @venvArgs
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $venvPython)) { throw '创建虚拟环境失败。' }
    Write-Ok $venv
}

function Invoke-Pip([string[]]$pipArgs) {
    & $venvPython -m pip @pipArgs
    if ($LASTEXITCODE -ne 0) { throw "pip 执行失败：pip $($pipArgs -join ' ')" }
}

Write-Step '更新 pip'
Invoke-Pip @('install', '--quiet', '--upgrade', 'pip', '-i', $PipMirror)

# ---------- 4. 装依赖 ----------
# 版本必须与模型仓库的 requirements.txt 一致：transformers 4.45.2 与 numpy 1.24.3 都是硬约束。
# mir_eval 也别漏：SheetSage2 的 tokenization_sheetsage2.py 会 import mir_eval.chord，
# 缺了会在加载模型时报 ModuleNotFoundError。
$deps = @(
    'transformers==4.45.2',
    'huggingface-hub==0.36.0',
    'safetensors==0.5.3',
    'numpy==1.24.3',
    'scipy==1.13.1',
    'mir_eval==0.8.2',
    'pretty_midi==0.2.10',
    'mido==1.3.3',
    'setuptools==78.1.1',
    'soundfile'
)
Write-Step '安装依赖（transformers / numpy / soundfile 等）'
Invoke-Pip (@('install', '-i', $PipMirror) + $deps)
Write-Ok '依赖安装完成'

Write-Step '安装 PyTorch 2.8.0'
$torchIndex = if ($CpuOnly) { $TorchFallbackIndex } else { $TorchIndex }
try {
    Invoke-Pip @('install', 'torch==2.8.0', 'torchaudio==2.8.0', '--index-url', $torchIndex)
} catch {
    Write-Warn2 "从 $torchIndex 安装失败，改用 CPU 版重试（转谱会明显变慢，但能跑）。"
    Invoke-Pip @('install', 'torch==2.8.0', 'torchaudio==2.8.0', '--index-url', $TorchFallbackIndex)
}
Write-Ok 'PyTorch 安装完成'

# ---------- 5. 放侧车脚本 ----------
Write-Step '复制侧车脚本'
Copy-Item (Join-Path $scriptDir 'transcribe.py') $Root -Force
Copy-Item (Join-Path $scriptDir 'fetch_models.py') $Root -Force
Write-Ok "transcribe.py / fetch_models.py → $Root"

# ---------- 6. 下载模型 ----------
if ($SkipModels) {
    Write-Warn2 '按 -SkipModels 跳过权重下载；之后可执行：'
    Write-Warn2 "  & '$venvPython' '$Root\fetch_models.py' --dest '$Root\models'"
} else {
    Write-Step '下载模型权重（约 2.6 GB，走 ModelScope）'
    & $venvPython (Join-Path $Root 'fetch_models.py') --dest (Join-Path $Root 'models')
    if ($LASTEXITCODE -ne 0) { throw '模型下载失败；可重跑本脚本，已下好的文件会跳过。' }
    Write-Ok '权重下载并校验完成'
}

# ---------- 7. 自检 ----------
Write-Step '环境自检'
& $venvPython (Join-Path $Root 'transcribe.py') --check --models-root (Join-Path $Root 'models')
if ($LASTEXITCODE -ne 0) { throw '自检失败，请看上面的报错。' }

Write-Host ''
Write-Host '安装完成。' -ForegroundColor Green
Write-Host "运行目录：$Root"
Write-Host '回到软件里点「音频转谱」即可；软件会自动找到这个目录，'
Write-Host "也可以设置环境变量 HARMONICA_AUDIO2SCORE=$Root 显式指认。"
