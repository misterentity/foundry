<#
  Authenticode-sign a file with the cert in the SIGN_PFX_BASE64 / SIGN_PASSWORD env vars.
  No-op (warning) if those aren't set. Used by the release workflow and runnable locally.
    pwsh build/sign.ps1 -Target build/AppPackages/FoundrySetup.exe
#>
param([Parameter(Mandatory = $true)][string]$Target)

$ErrorActionPreference = 'Stop'

if (-not $env:SIGN_PFX_BASE64) {
    Write-Warning "SIGN_PFX_BASE64 not set — skipping code-signing of $Target."
    exit 0
}

# locate signtool (newest Windows SDK)
$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) { $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source }
if (-not $signtool) { throw "signtool.exe not found (install the Windows SDK)." }

$pfx = Join-Path $env:TEMP "foundry-sign.pfx"
[IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:SIGN_PFX_BASE64))
try {
    $pwArgs = @()
    if ($env:SIGN_PASSWORD) { $pwArgs = @('/p', $env:SIGN_PASSWORD) }
    & $signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f $pfx @pwArgs $Target
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE) for $Target." }
    & $signtool verify /pa $Target | Out-Null
    Write-Host "Signed $Target"
}
finally {
    Remove-Item $pfx -Force -ErrorAction SilentlyContinue
}
