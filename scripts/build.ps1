$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet run --project tests/LanBridge.SmokeTests/LanBridge.SmokeTests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core smoke tests failed' }
    dotnet publish src/LanBridge.Desktop/LanBridge.Desktop.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishTrimmed=false -p:PublishSingleFile=false -o artifacts/publish
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed' }
    Copy-Item scripts/firewall.ps1 artifacts/publish/firewall.ps1 -Force
    Write-Host "Published to artifacts/publish" -ForegroundColor Green
    $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    if (-not $iscc) {
        $iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    }
    if (Test-Path $iscc) {
        & $iscc installer/LanBridge.iss
        if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
        Write-Host 'Installer generated in artifacts/setup' -ForegroundColor Green
    } else {
        Write-Warning 'Inno Setup 6 is not installed; publish succeeds but installer was not built.'
    }
} finally { Pop-Location }
