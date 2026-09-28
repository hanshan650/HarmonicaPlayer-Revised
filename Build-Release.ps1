param([switch]$WithAllScores)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet run --project .\Tests\ParserTests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Publishing cancelled.' }

    dotnet run --project .\Tests\WindowsSmoke\WindowsSmokeTests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Windows UI tests failed. Publishing cancelled.' }

    $product = 'HarmonicaPlayer-Revised'
    $version = '1.0'
    $buildStamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $outputDir = Join-Path $PSScriptRoot "bin\Release\publish-$version-$buildStamp"
    dotnet publish .\HarmonicaPlayer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $outputDir
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. ZIP was not created.' }
    if (!(Test-Path (Join-Path $outputDir 'HarmonicaPlayer.exe'))) { throw 'EXE missing.' }
    # 调试符号不随发布包分发
    Remove-Item (Join-Path $outputDir 'HarmonicaPlayer.pdb') -Force -ErrorAction SilentlyContinue

    Copy-Item .\rhythm-demo.txt $outputDir
    Copy-Item .\score-format-demo.txt $outputDir
    Copy-Item .\audio-midi-demo.txt $outputDir
    Copy-Item .\CONTRIBUTIONS.md $outputDir
    Copy-Item .\THIRD-PARTY-NOTICES.md $outputDir
    Copy-Item .\Assets\Harmonica\LICENSE-CC0.txt $outputDir
    Copy-Item .\NOTICE.md $outputDir
    Copy-Item .\README.md $outputDir
    Copy-Item .\AudioExamples $outputDir -Recurse
    Copy-Item -LiteralPath .\ai转谱模板.txt -Destination $outputDir
    Copy-Item -LiteralPath .\使用说明.txt -Destination $outputDir
    Copy-Item -LiteralPath '.\制谱说明.txt' -Destination $outputDir
    Copy-Item -LiteralPath '.\首次运行说明.txt' -Destination $outputDir
    if (Test-Path -LiteralPath '.\简谱' -PathType Container) {
        $scoreOutputDir = Join-Path $outputDir '简谱'
        New-Item -ItemType Directory -Path $scoreOutputDir -Force | Out-Null
        foreach ($category in 'TXT', 'MIDI', 'JSON') {
            $target = Join-Path $scoreOutputDir $category
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            $source = Join-Path '.\简谱' $category
            if ($category -eq 'JSON' -and !$WithAllScores) {
                # 第三方商业软件的曲库数据不随公开包分发，只放使用说明
                Copy-Item -LiteralPath (Join-Path $source 'README.txt') -Destination $target -Force -ErrorAction SilentlyContinue
                Write-Host '简谱\JSON：仅打包说明文件（要连同第三方曲谱一起打包，加 -WithAllScores）'
                continue
            }
            if (!(Test-Path -LiteralPath $source -PathType Container)) { continue }
            $scoreFiles = @(Get-ChildItem -LiteralPath $source -File)
            if ($scoreFiles.Count -eq 0) { Write-Warning "简谱\$category 内没有曲谱。"; continue }
            $scoreFiles | Copy-Item -Destination $target -Force
            Write-Host "已打包 简谱\$category：$($scoreFiles.Count) 个文件"
        }
    } else {
        Write-Warning '未找到简谱文件夹，本次仅打包内置示例谱。'
    }
    $zipSuffix = if ($WithAllScores) { '-full' } else { '' }
    $zipPath = Join-Path $PSScriptRoot "bin\Release\$product-v$version-win-x64$zipSuffix.zip"
    Compress-Archive -Path (Join-Path $outputDir '*') -DestinationPath $zipPath -Force
    Write-Host "ZIP: $zipPath"
    Write-Host 'Run the new EXE and verify hotkeys before publishing the ZIP.'
    explorer.exe $outputDir
}
finally { Pop-Location }
