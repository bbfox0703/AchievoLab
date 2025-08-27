# clean-xaml-locales.ps1
[CmdletBinding()]
param(
    [switch]$WhatIf
)

# 遞迴掃描這兩個根路徑底下的所有子資料夾
$roots = @(
    Join-Path $PSScriptRoot 'output\Debug'
    Join-Path $PSScriptRoot 'output\Release'
)

# 白名單（大小寫不敏感）
$keep = @('en-us','en-gb','zh-tw','ja-jp','ko-kr')

# 語系資料夾命名樣式（符合 zh-CN、sr-Cyrl-BA、ca-Es-VALENCIA 等）
$localeRegex = '^(?i)[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})+$'

# 需要「剛好存在」的兩個檔案（大小寫不敏感比對）
$mustFiles = @('Microsoft.ui.xaml.dll.mui','Microsoft.UI.Xaml.Phone.dll.mui')

function Should-DeleteFolder([IO.DirectoryInfo]$dir) {
    $name = $dir.Name
    if ($name -notmatch $localeRegex) { return $false }               # 不是語系命名
    if ($keep -contains $name.ToLowerInvariant()) { return $false }   # 白名單保留

    # 只檢查目錄本身（不遞迴）
    $files = Get-ChildItem -LiteralPath $dir.FullName -File -Force -ErrorAction SilentlyContinue
    $subDirs = Get-ChildItem -LiteralPath $dir.FullName -Directory -Force -ErrorAction SilentlyContinue

    # 必須沒有子資料夾
    if ($subDirs.Count -ne 0) { return $false }

    # 必須剛好只有 2 個檔案，且名稱為指定兩個（大小寫不敏感）
    if ($files.Count -ne 2) { return $false }

    $fileNames = $files.Name | ForEach-Object { $_.ToLowerInvariant() }
    $mustLower = $mustFiles | ForEach-Object { $_.ToLowerInvariant() }

    # 比對集合是否完全相等
    $setEqual = ($fileNames | Sort-Object) -join '|' -eq ($mustLower | Sort-Object) -join '|'
    return $setEqual
}

foreach ($root in $roots) {
    if (-not (Test-Path $root)) {
        Write-Host "Skip: not found $root" -ForegroundColor Yellow
        continue
    }

    Write-Host "Recursive scan: $root" -ForegroundColor Cyan
    $allDirs = Get-ChildItem -Path $root -Directory -Recurse -ErrorAction SilentlyContinue

    $toDelete = $allDirs | Where-Object { Should-DeleteFolder $_ }

    if (-not $toDelete) {
        Write-Host "  No match."
        continue
    }

    foreach ($d in $toDelete) {
        Write-Host ("  Removed: {0}" -f $d.FullName)
        if ($WhatIf) { continue }
        try {
            Remove-Item -LiteralPath $d.FullName -Recurse -Force -ErrorAction Stop
        } catch {
            Write-Host ("    Failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
        }
    }
}