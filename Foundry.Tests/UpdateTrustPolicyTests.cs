using Foundry.Core.Update;

namespace Foundry.Tests;

public class UpdateTrustPolicyTests
{
    // Unsigned build — the current distribution choice. There is no publisher to pin to, so the strict gate
    // cannot apply and refusing only forces a manual download of the SAME installer from the releases page.
    // The update is allowed (the user still confirms the install) and the decision is logged as unverified.
    //
    // This was set in d821bab, silently reverted by the f5b4f43 hardening commit, and the consequence
    // reached the app log: "refusing to auto-run the update" on a build that could never be signed, so the
    // app could never update itself. Signing the build re-engages the strict gate automatically.
    [Fact]
    public void UnsignedApp_IsAllowed_ButReportedAsUnverified()
    {
        var d = UpdateTrustPolicy.Decide(appThumbprint: null, installerAuthenticodeValid: false, installerThumbprint: null);
        Assert.True(d.Trusted);
        Assert.Contains("unsigned", d.Reason);
        Assert.Contains("unverified", d.Reason);
    }

    [Fact]
    public void UnsignedApp_IsAllowedEvenWhenTheInstallerIsAlsoUnsigned()
    {
        var d = UpdateTrustPolicy.Decide(appThumbprint: "", installerAuthenticodeValid: false, installerThumbprint: "");
        Assert.True(d.Trusted);
    }

    // The moment the app IS signed, every strict check applies again — that is the whole point of
    // "strict when signed", and it is what stops this from being a permanent hole.
    [Fact]
    public void SigningTheApp_ReEngagesTheStrictGate()
    {
        Assert.False(UpdateTrustPolicy.Decide("ABC123", false, "ABC123").Trusted);   // bad Authenticode
        Assert.False(UpdateTrustPolicy.Decide("ABC123", true, null).Trusted);        // unsigned installer
        Assert.False(UpdateTrustPolicy.Decide("ABC123", true, "DIFFERENT").Trusted); // wrong publisher
        Assert.True(UpdateTrustPolicy.Decide("ABC123", true, "ABC123").Trusted);     // same publisher
    }

    [Fact]
    public void SignedApp_InstallerFailsAuthenticode_IsRefused()
    {
        var d = UpdateTrustPolicy.Decide(appThumbprint: "ABC123", installerAuthenticodeValid: false, installerThumbprint: "ABC123");
        Assert.False(d.Trusted);
    }

    [Fact]
    public void SignedApp_InstallerUnsigned_IsRefused()
    {
        var d = UpdateTrustPolicy.Decide(appThumbprint: "ABC123", installerAuthenticodeValid: true, installerThumbprint: null);
        Assert.False(d.Trusted);
    }

    [Fact]
    public void SignedApp_DifferentPublisher_IsRefused()
    {
        var d = UpdateTrustPolicy.Decide(appThumbprint: "ABC123", installerAuthenticodeValid: true, installerThumbprint: "DEADBEEF");
        Assert.False(d.Trusted);
    }

    [Fact]
    public void SignedApp_SamePublisher_ValidAuthenticode_IsTrusted()
    {
        var d = UpdateTrustPolicy.Decide(appThumbprint: "ABC123", installerAuthenticodeValid: true, installerThumbprint: "abc123");
        Assert.True(d.Trusted);   // thumbprint match is case-insensitive
    }
}
