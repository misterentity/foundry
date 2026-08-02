using Foundry.Core.Update;

namespace Foundry.Tests;

public class UpdateTrustPolicyTests
{
    // Unsigned build: there's no publisher to pin to, so the updater must fail closed and direct users to the
    // releases page instead of auto-running a downloaded installer.
    [Fact]
    public void UnsignedApp_IsRefused_NoPublisherToVerify()
    {
        var d = UpdateTrustPolicy.Decide(appThumbprint: null, installerAuthenticodeValid: false, installerThumbprint: null);
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
