<#
  打包：CLI 与 GUI 打进同一个发布目录（GUI 通过子进程调用 CLI），
  产物为 self-contained 单文件，目标机不需要 .NET SDK/Runtime。
  用法：
    pwsh -File build/pack.ps1
    pwsh -File build/pack.ps1 -Velopack
    pwsh -File build/pack.ps1 -Velopack -GithubRepo owner/repo
#>
param(
    [string]$Configuration = 'Release',
    [string]$Runtime       = 'win-x64',
    [string]$Version       = '1.0.0',
    [string]$GithubRepo    = '',
    [string]$Channel       = 'win',
    [switch]$Velopack
)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'publish'
$stage   = Join-Path $publish 'app'
$icon    = Join-Path $root 'src/EssenceL10n.App/Assets/app.ico'

# 只清理本次会重建的目录；velopack/ 是独立产物，不能被无 -Velopack 的调用抹掉
Remove-Item $stage, (Join-Path $publish 'cli'), (Join-Path $publish 'gui') -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $publish -Filter 'EssenceL10n-*.zip' -File -ErrorAction SilentlyContinue | Remove-Item -Force
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$common = @(
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    # 必须注入：csproj 里写死的 Version 会让 exe 的 AssemblyInformationalVersion
    # 与 tag 脱节，Updater 读到的当前版本永远不变，更新检查就永远误判
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version"
)

Write-Host '==> 发布 CLI (EssenceL10n.Cli.exe)' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src/EssenceL10n.Cli/EssenceL10n.Cli.csproj') @common -o (Join-Path $publish 'cli')
if ($LASTEXITCODE -ne 0) { throw 'CLI 发布失败' }

Write-Host '==> 发布 GUI (EssenceL10n.exe)' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src/EssenceL10n.App/EssenceL10n.App.csproj') @common -o (Join-Path $publish 'gui')
if ($LASTEXITCODE -ne 0) { throw 'GUI 发布失败' }

Write-Host '==> 合并为单目录（前端 + 后端同包）' -ForegroundColor Cyan
Copy-Item (Join-Path $publish 'gui/*') $stage -Recurse -Force
Copy-Item (Join-Path $publish 'cli/EssenceL10n.Cli.exe') $stage -Force
Copy-Item (Join-Path $root 'catalogs') $stage -Recurse -Force

if (-not $GithubRepo) {
    # 未显式指定时回落到本仓库，保证产物里的更新通道可用
    $GithubRepo = 'StackAndPointer/Aoe4-Essence-Editor-Multilingual-Patcher'
}
$update = @{ githubRepo = $GithubRepo; channel = $Channel } | ConvertTo-Json
Set-Content -LiteralPath (Join-Path $stage 'update.json') -Value $update -Encoding UTF8

Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Encoding UTF8 -Value @"
Age of Empires IV - Essence Editor Multilingual Patcher
Version $Version
https://github.com/$GithubRepo

A version-independent localization patcher for the Age of Empires IV Essence Editor
(mod editor). Translations live in a catalog keyed by assembly + resource + key, so a
game update no longer invalidates the patch. Anything the catalog does not cover falls
back to the original English text.

QUICK START
  1. Run EssenceL10n.exe (the GUI).
  2. Choose the game folder - the one containing EssenceEditor.exe. Steam libraries are
     detected automatically; you can also browse manually. The tool does not need to be
     installed inside the game folder.
  3. Pick a patch language, click "Scan" to inspect coverage, then "Apply patch" to write.

Originals are backed up to <game folder>/EssenceL10n.backup/<timestamp>/ before writing.
This can be turned off in the GUI.

COMMAND LINE
  Every write goes through EssenceL10n.Cli.exe, which lives in this same folder:

    EssenceL10n.Cli.exe report "<game folder>" --catalog catalogs/zh-Hans/zh-Hans.jsonl --json
    EssenceL10n.Cli.exe apply  "<game folder>" --catalog catalogs/zh-Hans/zh-Hans.jsonl
    EssenceL10n.Cli.exe verify "<game folder>" --catalog catalogs/zh-Hans/zh-Hans.jsonl --json

  Run EssenceL10n.Cli.exe help for the full command list.

INCLUDED LANGUAGES
  catalogs/zh-Hans   Simplified Chinese   99.0%
  catalogs/zh-Hant   Traditional Chinese  99.0%
  catalogs/ja        Japanese             98.0%
  catalogs/ru        Russian              98.2%
  catalogs/es        Spanish              97.5%
  catalogs/de        German               94.3%
  catalogs/en        English (baseline)

No .NET runtime or SDK is required - everything is published self-contained.

This is fan-made tooling and is not affiliated with Relic Entertainment or World's Edge.
"@

Write-Host '==> 压缩 zip' -ForegroundColor Cyan
$zip = Join-Path $publish ("EssenceL10n-" + $Version + "-" + $Runtime + ".zip")
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force

Get-ChildItem $stage -File | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
Write-Host ("zip: {0} MB -> {1}" -f [math]::Round((Get-Item $zip).Length / 1MB, 1), $zip)

if ($Velopack) {
    Write-Host '==> Velopack 打包' -ForegroundColor Cyan
    $env:PATH = "$env:USERPROFILE/.dotnet/tools;$env:PATH"
    if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
        dotnet tool install --global vpk | Out-Null
        $env:PATH = "$env:USERPROFILE/.dotnet/tools;$env:PATH"
    }
    $vpkOut = Join-Path $publish 'velopack'
    New-Item -ItemType Directory -Force -Path $vpkOut | Out-Null
    $vpkArgs = @(
        'pack',
        '--packId', 'EssenceL10n',
        '--packVersion', $Version,
        '--packDir', $stage,
        '--mainExe', 'EssenceL10n.exe',
        '--packTitle', 'Age of Empires IV - Essence Editor Multilingual Patcher',
        '--packAuthors', 'EssenceL10n',
        '--icon', $icon,
        '--runtime', $Runtime,
        '--channel', $Channel,
        '--outputDir', $vpkOut
    )
    & vpk @vpkArgs
    if ($LASTEXITCODE -ne 0) { throw 'Velopack 打包失败' }

    if ($GithubRepo) {
        $msg = 'vpk upload github --repoUrl https://github.com/' + $GithubRepo + ' --token $env:GITHUB_TOKEN --publish --tag v' + $Version
        Write-Host ('GitHub Release 上传命令：' + $msg) -ForegroundColor Yellow
    }
}

Write-Host "完成：$publish" -ForegroundColor Green
