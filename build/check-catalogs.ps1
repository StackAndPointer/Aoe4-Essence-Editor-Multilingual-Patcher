<#
  校验语言目录：
    - 与 en 基准目录 key 集合一致（Assembly + Resource + Key）
    - 无空 Value（State 3 的技术标识除外）
    - 占位符 {0} {1} ... 两侧一致

  用法：pwsh -File build/check-catalogs.ps1 [-Reference en]
#>
param([string]$Reference = 'en', [string]$CatalogsRoot = 'catalogs')

$ErrorActionPreference = 'Stop'
$refFile = Join-Path $CatalogsRoot "$Reference/$Reference.jsonl"
if (-not (Test-Path $refFile)) {
    Write-Host "::error::missing reference catalog $refFile"
    exit 1
}

function Read-Catalog([string]$path) {
    @(Get-Content $path -Encoding UTF8 |
        Where-Object { $_.Trim() } |
        ForEach-Object { $_ | ConvertFrom-Json })
}

function Key-Of($unit) { "$($unit.Assembly)|$($unit.Resource)|$($unit.Key)" }

$referenceKeys = @(Read-Catalog $refFile | ForEach-Object { Key-Of $_ })
Write-Host ("reference: {0} ({1} keys)" -f $Reference, $referenceKeys.Count)

$fail = 0
$checked = 0

foreach ($dir in Get-ChildItem $CatalogsRoot -Directory | Sort-Object Name) {
    $lang = $dir.Name
    if ($lang -eq $Reference) { continue }

    $file = Join-Path $dir.FullName "$lang.jsonl"
    if (-not (Test-Path $file)) {
        Write-Host "::error::missing catalog $file"
        $fail = 1
        continue
    }

    $units = @(Read-Catalog $file)
    $keys = @($units | ForEach-Object { Key-Of $_ })

    $diff = Compare-Object $referenceKeys $keys
    if ($diff) {
        Write-Host ("::error::{0} key set differs from {1} ({2} diffs)" -f $lang, $Reference, $diff.Count)
        $fail = 1
    }

    $empty = @($units | Where-Object { -not $_.Value -and $_.State -ne 3 }).Count
    if ($empty -gt 0) {
        Write-Host ("::error::{0} has {1} empty values" -f $lang, $empty)
        $fail = 1
    }

    # 只比对两侧都有原文的单元；早期 bootstrap 遗留条目 Source 为空，无从对照
    $comparable = @($units | Where-Object { $_.Source -and $_.Value })
    $drift = @($comparable | Where-Object {
        $src = ([regex]::Matches([string]$_.Source, '\{\d+\}') | ForEach-Object { $_.Value } | Sort-Object) -join ','
        $val = ([regex]::Matches([string]$_.Value, '\{\d+\}') | ForEach-Object { $_.Value } | Sort-Object) -join ','
        $src -ne $val
    }).Count
    if ($drift -gt 0) {
        Write-Host ("::error::{0} has {1} placeholder mismatches" -f $lang, $drift)
        $fail = 1
    }

    Write-Host ("{0}: {1} units, {2} empty, {3} placeholder drift" -f $lang, $units.Count, $empty, $drift)
    $checked++
}

Write-Host ("checked {0} catalogs, fail={1}" -f $checked, $fail)
exit $fail
