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

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$common = @(
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
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
《帝国时代 IV》Essence Editor（模组编辑器）多语言热补丁工具
Age of Empires IV - Essence Editor Multilingual Patcher $Version

用法
  1. 双击 EssenceL10n.exe（GUI），选择游戏根目录（含 EssenceEditor.exe 的那一层）
  2. 选择补丁语言，点「扫描」看覆盖率，点「生成补丁」写入
  3. 写入前默认备份到 <游戏目录>/EssenceL10n.backup/<时间戳>/

命令行（同一目录下）
  EssenceL10n.Cli.exe report "<游戏目录>" --catalog catalogs/zh-Hans/zh-Hans.jsonl
  EssenceL10n.Cli.exe apply  "<游戏目录>" --catalog catalogs/zh-Hans/zh-Hans.jsonl

不需要安装 .NET 运行时。
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
        '--outputDir', (Join-Path $publish 'velopack')
    )
    & vpk @vpkArgs
    if ($LASTEXITCODE -ne 0) { throw 'Velopack 打包失败' }

    if ($GithubRepo) {
        $msg = 'vpk upload github --repoUrl https://github.com/' + $GithubRepo + ' --token $env:GITHUB_TOKEN --publish --tag v' + $Version
        Write-Host ('GitHub Release 上传命令：' + $msg) -ForegroundColor Yellow
    }
}

Write-Host "完成：$publish" -ForegroundColor Green
