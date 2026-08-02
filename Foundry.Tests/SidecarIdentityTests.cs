using System.Net;
using Foundry.Core.Sidecar;

namespace Foundry.Tests;

// The hole this closes, observed live on a dev machine: port 8731 was held by
// C:\Program Files\Foundry\sidecar\foundry-cad.exe (an older installed build). SidecarHost probed that
// fixed port and adopted ANY listener whose /health body contained "service" and "foundry-cad" — a
// constant with no version and no secret. So a build from the working tree silently served geometry
// from code that was not in the working tree, while the status bar read "connected".
public class SidecarIdentityTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    private static string Health(string service = "foundry-cad", string? token = Token) =>
        token is null
            ? $"{{\"status\":\"ok\",\"service\":\"{service}\",\"kernel\":\"manifold\"}}"
            : $"{{\"status\":\"ok\",\"service\":\"{service}\",\"token\":\"{token}\",\"kernel\":\"manifold\"}}";

    [Fact]
    public void AcceptsOurOwnChild() =>
        Assert.True(SidecarIdentity.Accept(Health(), Token));

    // The exact body the previously-installed sidecar returns.
    [Fact]
    public void RefusesAForeignFoundrySidecarWithNoToken() =>
        Assert.False(SidecarIdentity.Accept(
            "{\"status\":\"ok\",\"service\":\"foundry-cad\",\"kernel\":\"builtin\"}", Token));

    [Fact]
    public void RefusesAFoundrySidecarCarryingSomeoneElsesToken() =>
        Assert.False(SidecarIdentity.Accept(Health(token: "ffffffffffffffffffffffffffffffff"), Token));

    [Theory]
    [InlineData("not-foundry")]
    [InlineData("")]
    public void RefusesANonFoundryService(string service) =>
        Assert.False(SidecarIdentity.Accept(Health(service), Token));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"service\":")]
    public void RefusesGarbage(string body) =>
        Assert.False(SidecarIdentity.Accept(body, Token));

    // A token is a secret, so comparison must be exact — not a prefix or case-insensitive match.
    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdefX")]
    public void RefusesNearMissTokens(string token) =>
        Assert.False(SidecarIdentity.Accept(Health(token: token), Token));

    // Empty expected token = the explicit FOUNDRY_SIDECAR_URL override, where the operator has told us
    // which process to use. Any Foundry sidecar is acceptable there — but still not a non-Foundry one.
    [Fact]
    public void ExplicitOverride_AcceptsAnyFoundrySidecar()
    {
        Assert.True(SidecarIdentity.Accept(Health(token: null), ""));
        Assert.False(SidecarIdentity.Accept(Health("something-else", token: null), ""));
    }

    [Fact]
    public void TokensAreUniquePerSpawn() =>
        Assert.NotEqual(SidecarIdentity.NewToken(), SidecarIdentity.NewToken());

    // End-to-end over real HTTP: stand up a listener that impersonates the installed sidecar exactly,
    // and assert the client refuses it.
    [Fact]
    public async Task ClientRefusesAnImpostorListeningOnTheWire()
    {
        var port = FreePort();
        using var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();

        var serving = Task.Run(async () =>
        {
            try
            {
                var ctx = await http.GetContextAsync();
                var bytes = System.Text.Encoding.UTF8.GetBytes(
                    "{\"status\":\"ok\",\"service\":\"foundry-cad\",\"kernel\":\"builtin\"}");
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch { /* listener stopped */ }
        });

        try
        {
            var client = new SidecarClient($"http://127.0.0.1:{port}", expectedToken: Token);
            Assert.False(await client.HealthAsync(), "adopted a sidecar it did not spawn");
        }
        finally { http.Stop(); await serving; }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
