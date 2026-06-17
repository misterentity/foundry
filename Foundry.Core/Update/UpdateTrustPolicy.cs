namespace Foundry.Core.Update;

/// <summary>Outcome of the updater trust check: whether to run a downloaded installer, and why.</summary>
public sealed record UpdateDecision(bool Trusted, string Reason);

/// <summary>
/// Pure trust policy for an auto-downloaded installer, separated from the Win32/X509 plumbing so it can be
/// unit-tested. The rule is FAIL-CLOSED: an installer is only run when the running app is itself signed AND
/// the installer carries a valid Authenticode signature from the SAME publisher (thumbprint pin). When the
/// running app is UNSIGNED we cannot establish a publisher to pin to, so the update is REFUSED (the caller
/// falls back to opening the releases page for a manual, human-in-the-loop install) — never silently run an
/// unverified binary. Sign the app + installer to enable seamless auto-update.
/// </summary>
public static class UpdateTrustPolicy
{
    /// <param name="appThumbprint">Authenticode thumbprint of the running app's signer, or null if the app is unsigned.</param>
    /// <param name="installerAuthenticodeValid">Did WinVerifyTrust pass on the downloaded installer?</param>
    /// <param name="installerThumbprint">Thumbprint of the installer's signer, or null if it is unsigned/unreadable.</param>
    public static UpdateDecision Decide(string? appThumbprint, bool installerAuthenticodeValid, string? installerThumbprint)
    {
        if (string.IsNullOrEmpty(appThumbprint))
            return new(false, "running app is unsigned — can't verify the update's publisher; update via the releases page instead of auto-running an unverified installer");
        if (!installerAuthenticodeValid)
            return new(false, "downloaded installer failed Authenticode verification — refusing to run");
        if (string.IsNullOrEmpty(installerThumbprint))
            return new(false, "downloaded installer is not signed — refusing to run");
        if (!string.Equals(installerThumbprint, appThumbprint, StringComparison.OrdinalIgnoreCase))
            return new(false, $"installer signer {installerThumbprint} != app signer {appThumbprint} — refusing to run");
        return new(true, "installer verified: valid Authenticode from the same publisher as the running app");
    }
}
