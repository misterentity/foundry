using Foundry.Core.Security;

namespace Foundry.Tests;

public class CredentialMaskTests
{
    [Theory]
    [InlineData("sk1234567")]        // 9
    [InlineData("sk12345678")]       // 10
    [InlineData("0123456789a")]      // 11
    [InlineData("shortkey1")]        // 9
    public void Mask_NeverRevealsMostOfAShortSecret(string secret)
    {
        // The bug: a 9–11 char key was shown almost entirely (head 7 + tail 4 overlap). A mask must keep a
        // meaningful hidden middle so the raw key can't be reconstructed from the display.
        var masked = CredentialStore.Mask(secret);
        var revealed = masked.Replace("•", "").Replace("…", "");
        Assert.True(revealed.Length <= secret.Length - 4, $"masked '{masked}' reveals too much of a {secret.Length}-char secret");
        Assert.DoesNotContain(secret, masked);
    }

    [Fact]
    public void Mask_LongKey_ShowsFourHeadFourTail_WithHiddenMiddle()
    {
        var masked = CredentialStore.Mask("sk-ant-api03-abcdefghijklmnop");
        Assert.StartsWith("sk-a", masked);
        Assert.EndsWith("mnop", masked);
        Assert.Contains("…", masked);
    }

    [Fact]
    public void Mask_EmptyOrNull_ShowsDash()
    {
        Assert.Equal("—", CredentialStore.Mask(null));
        Assert.Equal("—", CredentialStore.Mask(""));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(40)]
    public void Mask_AlwaysHidesAtLeastFourCharsForAnyNonTrivialSecret(int len)
    {
        var secret = new string('x', len);
        var masked = CredentialStore.Mask(secret);
        var revealed = masked.Replace("•", "").Replace("…", "").Length;
        Assert.True(revealed <= Math.Max(0, len - 4), $"len {len}: revealed {revealed}");
    }
}

// Round-trips a secret through the REAL Windows Credential Manager (per-user, DPAPI) — the storage path that
// was previously green-by-omission. Windows-only; uses a unique throwaway target and always cleans up.
public class CredentialStoreRoundTripTests
{
    [Fact]
    public void SaveReadDelete_RoundTrips()
    {
        if (!OperatingSystem.IsWindows()) return;

        var store = new CredentialStore();
        var target = $"Foundry:Test:{Guid.NewGuid():N}";
        var secret = "sk-ant-" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(store.Read(target));            // not there yet
            store.Save(target, secret);
            Assert.Equal(secret, store.Read(target));   // exact round-trip
            Assert.True(store.Exists(target));
            store.Delete(target);
            Assert.Null(store.Read(target));            // gone
            Assert.False(store.Exists(target));
        }
        finally { try { store.Delete(target); } catch { } }
    }

    [Fact]
    public void Save_OverwritesExistingSecret()
    {
        if (!OperatingSystem.IsWindows()) return;

        var store = new CredentialStore();
        var target = $"Foundry:Test:{Guid.NewGuid():N}";
        try
        {
            store.Save(target, "first");
            store.Save(target, "second");
            Assert.Equal("second", store.Read(target));
        }
        finally { try { store.Delete(target); } catch { } }
    }
}
