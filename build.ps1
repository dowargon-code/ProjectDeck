$ErrorActionPreference = "Stop"

# ProjectDeck をビルドして dist\ に配布物を作る
#   dist\ProjectDeck\                 ProjectDeck.exe、deckctl.exe、settings.toml（config\ から）、README.md、THIRD_PARTY_NOTICES.md、grokbot\SKILL.md
#   dist\ProjectDeck-v<版>.zip        上のフォルダの中身
# 版は Directory.Build.props の <Version>。
# Windows PowerShell 5.1 が日本語のコメントを正しく読むよう、このファイルは BOM 付き UTF-8 で保存する
$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ProjectDir

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet が見つかりません。.NET 10 SDK をインストールしてください。"
}

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "失敗しました: dotnet $($Arguments -join ' ')" }
}

$AppName = "ProjectDeck"
$Runtime = "win-x64"
$Publish = Join-Path $ProjectDir "build\publish"

# テスト（失敗したら配布物を作らない）
Invoke-Dotnet @("test", "ProjectDeck.slnx", "-c", "Release", "--nologo")

# 本体と deckctl を同じフォルダへ発行（ProjectDeck.Core.dll は共通）
# フレームワーク依存（.NET 10 デスクトップランタイムが必要）＋ ReadyToRun で起動を速くする
if (Test-Path $Publish) { Remove-Item -Recurse -Force $Publish }
foreach ($proj in @("src\ProjectDeck\ProjectDeck.csproj", "src\deckctl\deckctl.csproj")) {
    Invoke-Dotnet @("publish", $proj, "-c", "Release", "-r", $Runtime, "--self-contained", "false",
        "-p:PublishReadyToRun=true", "-o", $Publish, "--nologo")
}

$Version = (& dotnet msbuild "src\ProjectDeck.Core\ProjectDeck.Core.csproj" -getProperty:Version).Trim()
if (-not $Version) { throw "版数を Directory.Build.props から読めませんでした。" }

$Dist = Join-Path $ProjectDir "dist"
$App = Join-Path $Dist $AppName
if (Test-Path $App) { Remove-Item -Recurse -Force $App }
New-Item -ItemType Directory -Force $App | Out-Null
Copy-Item (Join-Path $Publish "*") $App -Recurse -Exclude "*.pdb"
Copy-Item "README.md", "THIRD_PARTY_NOTICES.md" $App
Copy-Item "config\*" $App
# Grok Bot に読み込ませるスキル（grokbot\SKILL.md）
New-Item -ItemType Directory -Force (Join-Path $App "grokbot") | Out-Null
Copy-Item "grokbot\SKILL.md" (Join-Path $App "grokbot")

$Zip = Join-Path $Dist "$AppName-v$Version.zip"
if (Test-Path $Zip) { Remove-Item -Force $Zip }
Compress-Archive -Path (Join-Path $App "*") -DestinationPath $Zip
Write-Host "Generated: $App"
Write-Host "Generated: $Zip"
