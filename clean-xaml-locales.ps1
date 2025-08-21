# clean-xaml-locales.ps1
[CmdletBinding()]
param(
    [switch]$WhatIf
)

# 需要清理的根路徑
$baseDebug = Join-Path $PSScriptRoot 'output\Debug\x64\net8.0-windows10.0.22621.0'
$baseRelease = Join-Path $PSScriptRoot 'output\Release\x64\net8.0-windows10.0.22621.0'
$projects = @('AnSAM', 'RunGame', 'MyOwnGames')

$roots = @()
foreach ($base in @($baseDebug, $baseRelease)) {
    foreach ($project in $projects) {
        $roots += Join-Path $base $project
    }
}

# 要保留的語系（大小寫不敏感）
$keep = @('en-us','en-gb','zh-tw','ja-jp','ko-kr')

# 語系資料夾命名模式
$localeRegex = '^(?i)[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})+$'

function Should-DeleteFolder([IO.DirectoryInfo]$dir) {
    $name = $dir.Name
    if ($name -notmatch $localeRegex) { return $false }
    if ($keep -contains $name.ToLowerInvariant()) { return $false }

    # 條件：必須同時存在這兩個檔案
    $file1 = Join-Path $dir.FullName 'Microsoft.ui.xaml.dll.mui'
    $file2 = Join-Path $dir.FullName 'Microsoft.UI.Xaml.Phone.dll.mui'

    if ((Test-Path $file1) -and (Test-Path $file2)) {
        return $true
    }
    return $false
}

foreach ($root in $roots) {
    if (-not (Test-Path $root)) {
        Write-Host "略過：找不到 $root" -ForegroundColor Yellow
        continue
    }

    Write-Host "掃描：$root" -ForegroundColor Cyan
    $candidates = Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue

    $toDelete = @()
    foreach ($d in $candidates) {
        if (Should-DeleteFolder $d) { $toDelete += $d }
    }

    if (-not $toDelete) {
        Write-Host "  沒有符合條件要刪除的資料夾。"
        continue
    }

    foreach ($d in $toDelete) {
        Write-Host ("  移除：{0}" -f $d.FullName)
        if ($WhatIf) { continue }
        try {
            Remove-Item -LiteralPath $d.FullName -Recurse -Force -ErrorAction Stop
        } catch {
            Write-Host ("    失敗：{0}" -f $_.Exception.Message) -ForegroundColor Red
        }
    }
}

Write-Host "全部完成。"
