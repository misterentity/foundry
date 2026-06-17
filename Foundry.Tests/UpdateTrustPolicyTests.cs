using Foundry.Core.Update;

namespace Foundry.Tests;

public class UpdateTrustPolicyTests
{
    // The headline fix: an UNSIGNED running app must REFUSE the update (fail-closed), not run an unverified
    // installer. This is the case that was previously fail-open (auto-run an unverified binary = RCE exposure).
    [Fact]
    public void UnsignedApp_IsRefused()
    {
        var d = UpdateTrustPolicy.Decide(appThumbprint: null, installerAuthenticodeValid: true, installerThumbprint: "ABC123");
        Assert.False(d.Trusted);
        Assert.Contains("unsigned", d.Reason);
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
