# Seed default sites (1 factory + 3 sales centers) via BongoTex API.
# API must be running. Usage:
#   .\seed-default-sites.ps1
#   .\seed-default-sites.ps1 http://localhost:5080
$BaseUrl = "http://localhost:5080"
if ($args.Count -ge 1 -and $args[0]) {
    $BaseUrl = [string]$args[0]
}
$trimmed = $BaseUrl.TrimEnd(@('/', '\'))
$uri = "$trimmed/api/setup/sites/default"
Write-Host "POST $uri"
try {
    $response = Invoke-RestMethod -Method Post -Uri $uri -ContentType "application/json" -Body "{}"
    $json = $response | ConvertTo-Json -Compress
    Write-Host "OK: $json"
}
catch {
    $msg = $_.Exception.Message
    if ($_.ErrorDetails.Message) {
        $msg = [string]$_.ErrorDetails.Message
    }
    Write-Host "FAILED: $msg"
    Write-Host "Tip: Run dotnet run first; pass URL as arg if port differs."
    Write-Host "Tip: Sites already configured means Sites table has rows."
    exit 1
}