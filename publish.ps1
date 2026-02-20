#!/usr/bin/env pwsh
# AchievoLab Avalonia AOT Publish Script

Write-Host "========================================"
Write-Host " AchievoLab Native AOT Publish"
Write-Host "========================================"
Write-Host ""

$projects = @("AnSAM", "RunGame", "MyOwnGames")
$failed = $false

for ($i = 0; $i -lt $projects.Count; $i++) {
    $proj = $projects[$i]
    $num = $i + 1
    Write-Host "[$num/$($projects.Count)] Publishing $proj..."

    dotnet publish "$proj/$proj.csproj" `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishAot=true `
        -o "publish/$proj"

    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "*** Publish failed for $proj with exit code $LASTEXITCODE ***" -ForegroundColor Red
        $failed = $true
        break
    }
    Write-Host ""
}

if (-not $failed) {
    Write-Host "Removing *.pdb files..."
    Get-ChildItem publish -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

    Write-Host ""
    Write-Host "========================================"
    Write-Host " All projects published successfully!"
    Write-Host "========================================"
}
