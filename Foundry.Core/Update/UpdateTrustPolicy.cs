namespace Foundry.Core.Update;

/// <summary>Outcome of the updater trust check: whether to run a downloaded installer, and why.</summary>
public sealed record UpdateDecision(bool Trusted, string Reason);

/// <summary>
/// Pure trust policy for an auto-downloaded installer, separated from the Win32/X509 plumbing so it can be
/// unit-tested. STRICT-WHEN-SIGNED: when the running app IS signed, the installer must carry a valid Authenticode
/// signature from the SAME publisher (thumbprint pin) or it is refused. When the app is UNSIGNED — the current
/// distribution choice — there is no publisher to pin to, so the strict gate can't apply; the update is allowed
/// so the tray "Check for updates" stays one-click (the user still confirms the install). The decision is logged
/// either way. Sign the app + installer to automatically engage the strict publisher-pinned gate.
/// </summary>
public static class UpdateTrustPolicy
{
    /// <param name="appThumbprint">Authenticode thumbprint of the running app's signer, or null if the app is unsigned.</param>
    /// <param name="installerAuthenticodeValid">Did WinVerifyTrust pass on the downloaded installer?</param>
    /// <param name="installerThumbprint">Thumbprint of the installer's signer, or null if it is unsigned/unreadable.</param>
    public static UpdateDecision Decide(string? appThumbprint, bool installerAuthenticodeValid, string? installerThumbprint)
    {
        if (string.IsNullOrEmpty(appThumbprint))
            return new(true, "running app is unsigned — no publisher to verify against; running the update unverified (sign the build to enforce publisher verification)");
        if (!installerAuthenticodeValid)
            return new(false, "downloaded installer failed Authenticode verification — refusing to run");
        if (string.IsNullOrEmpty(installerThumbprint))
            return new(false, "downloaded installer is not signed — refusing to run");
        if (!string.Equals(installerThumbprint, appThumbprint, StringComparison.OrdinalIgnoreCase))
            return new(false, $"installer signer {installerThumbprint} != app signer {appThumbprint} — refusing to run");
        return new(true, "installer verified: valid Authenticode from the same publisher as the running app");
    }
}
