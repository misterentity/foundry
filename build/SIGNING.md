# Code signing

The release pipeline signs `Foundry.exe` and `FoundrySetup.exe` with Authenticode **when a
certificate is configured** — otherwise the signing steps are skipped (unsigned builds, which
trip Windows SmartScreen with "Windows protected your PC").

## Why it matters
Unsigned installers show a SmartScreen warning and "Unknown publisher". An **OV** cert removes
"unknown publisher" and builds reputation over time; an **EV** cert gets instant SmartScreen trust.

## What you need
A code-signing certificate as a password-protected `.pfx` from a CA (e.g. DigiCert, Sectigo,
SSL.com). EV certs are usually issued on hardware tokens — for CI you'd use the CA's cloud-signing
(e.g. DigiCert KeyLocker / Azure Trusted Signing) instead of a raw `.pfx`; see "Cloud signing" below.

## Enable signing in CI (PFX path)
1. Base64-encode the cert:
   ```powershell
   [Convert]::ToBase64String([IO.File]::ReadAllBytes("foundry.pfx")) | Set-Content cert.b64
   ```
2. In the GitHub repo → Settings → Secrets and variables → Actions, add:
   - `SIGN_PFX_BASE64` = contents of `cert.b64`
   - `SIGN_PASSWORD` = the .pfx password
3. Next tagged release (`git tag vX.Y.Z && git push --tags`) signs both binaries automatically
   (`build/sign.ps1`, timestamped via DigiCert's RFC-3161 server).

## Sign locally
```powershell
$env:SIGN_PFX_BASE64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes("foundry.pfx"))
$env:SIGN_PASSWORD   = "…"
pwsh build/sign.ps1 -Target build/AppPackages/FoundrySetup.exe
```

## Cloud signing (EV / token-based)
Raw `.pfx` won't work for EV/token certs. Swap the `Sign …` steps for the CA's GitHub Action
(e.g. `azure/trusted-signing-action` or DigiCert KeyLocker's `ssm-code-signing`) — they expose the
same `signtool` flow without a local key. The rest of the pipeline is unchanged.
