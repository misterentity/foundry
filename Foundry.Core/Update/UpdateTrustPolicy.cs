namespace Foundry.Core.Update;

/// <summary>Outcome of the updater trust check: whether to run a downloaded installer, and why.</summary>
public sealed record UpdateDecision(bool Trusted, string Reason);

/// <summary>
/// Pure trust policy for an auto-downloaded installer, separated from the Win32/X509 plumbing so it can be
/// unit-tested. Fail closed: the updater only runs an installer when the running app is signed and the downloaded
/// installer has a valid Authenticode signature from the same publisher (thumbprint pin). Unsigned builds can still
/// check for updates, but they must direct the user to the releases page instead of auto-running a downloaded file.
/// </summary>
public static class UpdateTrustPolicy
{
    /// <param name="appThumbprint">Authenticode thumbprint of the running app's signer, or null if the app is unsigned.</param>
    /// <param name="installerAuthenticodeValid">Did WinVerifyTrust pass on the downloaded installer?</param>
    /// <param name="installerThumbprint">Thumbprint of the installer's signer, or null if it is unsigned/unreadable.</param>
    public static UpdateDecision Decide(string? appThumbprint, bool installerAuthenticodeValid, string? installerThumbprint)
    {
        if (string.IsNullOrEmpty(appThumbprint))
            return new(false, "running app is unsigned — no publisher to verify against; refusing to auto-run the update");
        if (!installerAuthenticodeValid)
            return new(false, "downloaded installer failed Authenticode verification — refusing to run");
        if (string.IsNullOrEmpty(installerThumbprint))
            return new(false, "downloaded installer is not signed — refusing to run");
        if (!string.Equals(installerThumbprint, appThumbprint, StringComparison.OrdinalIgnoreCase))
            return new(false, $"installer signer {installerThumbprint} != app signer {appThumbprint} — refusing to run");
        return new(true, "installer verified: valid Authenticode from the same publisher as the running app");
    }
}
