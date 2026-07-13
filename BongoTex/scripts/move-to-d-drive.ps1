# Move BongoTex from C:\Users\USER\BongoTex to D:\BongoTex
# Usage: powershell -ExecutionPolicy Bypass -File "C:\Users\USER\BongoTex\scripts\move-to-d-drive.ps1"

$ErrorActionPreference = "Stop"
$Source = "C:\Users\USER\BongoTex"
$Dest = "D:\BongoTex"

Write-Host "=== Step 1: Stop processes on port 5080 ==="
$conn = Get-NetTCPConnection -LocalPort 5080 -ErrorAction SilentlyContinue
if ($conn) {
    $conn.OwningProcess | Select-Object -Unique | ForEach-Object {
        Write-Host "Stopping PID $_"
        Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Host "Nothing listening on 5080"
}

Write-Host "=== Step 2: Verify source ==="
if (-not (Test-Path $Source)) {
    throw "Source not found: $Source"
}
Write-Host "Source OK: $Source"

Write-Host "=== Step 3: Verify D: drive ==="
if (-not (Test-Path "D:\")) {
    throw "D:\ drive not found"
}

Write-Host "=== Step 4: Copy to D:\BongoTex ==="
$destHasContent = (Test-Path $Dest) -and ((Get-ChildItem $Dest -Force -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0)
if ($destHasContent) {
    Write-Host "Destination already has content, skipping copy"
} else {
    if (Test-Path $Dest) { Remove-Item $Dest -Recurse -Force }
    Copy-Item -Path $Source -Destination $Dest -Recurse -Force
    Write-Host "Copy completed"
}

Write-Host "=== Step 5: dotnet clean + restore ==="
Push-Location "$Dest\src\BongoTex.Api"
dotnet clean
Pop-Location
Push-Location $Dest
dotnet restore
Pop-Location
Write-Host "Restore OK"

Write-Host "=== Step 6: Build ==="
Push-Location "$Dest\src\BongoTex.Api"
dotnet build --no-restore
Pop-Location
Write-Host "Build OK"

Write-Host "=== Step 7: HTTP test (start API briefly) ==="
$job = Start-Job -ScriptBlock {
    Set-Location "D:\BongoTex\src\BongoTex.Api"
    dotnet run --no-build
}
$deadline = (Get-Date).AddMinutes(3)
$listening = $false
while ((Get-Date) -lt $deadline) {
    if (Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue) {
        $listening = $true
        break
    }
    Start-Sleep -Seconds 2
}
if (-not $listening) {
    Stop-Job $job -ErrorAction SilentlyContinue
    Remove-Job $job -Force -ErrorAction SilentlyContinue
    throw "API did not start on port 5080 within 3 minutes"
}
Write-Host "API listening on 5080"

$r1 = Invoke-WebRequest -Uri "http://localhost:5080" -UseBasicParsing -TimeoutSec 30
Write-Host "localhost: $($r1.StatusCode)"
try {
    $r2 = Invoke-WebRequest -Uri "http://192.168.10.207:5080" -UseBasicParsing -TimeoutSec 30
    Write-Host "192.168.10.207: $($r2.StatusCode)"
} catch {
    Write-Host "192.168.10.207: FAILED - $($_.Exception.Message) (firewall may need rule)"
}

Stop-Job $job -ErrorAction SilentlyContinue
Remove-Job $job -Force -ErrorAction SilentlyContinue
Get-NetTCPConnection -LocalPort 5080 -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique |
    ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }

Write-Host "=== Step 8: Remove old folder ==="
if (Test-Path $Source) {
    Remove-Item -Path $Source -Recurse -Force
    Write-Host "Deleted $Source"
} else {
    Write-Host "Source already removed"
}

Write-Host ""
Write-Host "DONE. New location: D:\BongoTex"
Write-Host "Run the API with:"
Write-Host "  cd D:\BongoTex\src\BongoTex.Api"
Write-Host "  dotnet run"
