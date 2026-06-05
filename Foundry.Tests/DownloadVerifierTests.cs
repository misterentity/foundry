using System.IO.Compression;
using System.Net;
using Foundry.Core.Pcb;
using Foundry.Core.Provisioning;

namespace Foundry.Tests;

public class DownloadVerifierTests
{
    // ---- a tiny in-process HttpClient that serves fixed bytes (no network) -------------------------
    private sealed class BytesHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        public BytesHandler(byte[] body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) });
    }

    private static string Sha256Hex(byte[] b) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));

    private static string TempPath(string ext) =>
        Path.Combine(Path.GetTempPath(), "foundry_dv_" + Guid.NewGuid().ToString("N")[..8] + ext);

    [Fact]
    public async Task DownloadVerified_MatchingHash_WritesFile()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("foundry-artifact-bytes");
        var dest = TempPath(".bin");
        using var http = new HttpClient(new BytesHandler(body));
        try
        {
            await DownloadVerifier.DownloadVerifiedAsync(http, "https://x/y", dest, Sha256Hex(body));
            Assert.True(File.Exists(dest));
            Assert.Equal(body, await File.ReadAllBytesAsync(dest));
            Assert.False(File.Exists(dest + ".part"));
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task DownloadVerified_Mismatch_DeletesPart_AndThrows()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("real-bytes");
        var dest = TempPath(".bin");
        using var http = new HttpClient(new BytesHandler(body));
        try
        {
            await Assert.ThrowsAsync<IntegrityException>(() =>
                DownloadVerifier.DownloadVerifiedAsync(http, "https://x/y", dest, Sha256Hex(System.Text.Encoding.UTF8.GetBytes("tampered"))));
            Assert.False(File.Exists(dest), "destination must not be written on mismatch");
            Assert.False(File.Exists(dest + ".part"), ".part must be cleaned up on mismatch");
        }
        finally { if (File.Exists(dest)) File.Delete(dest); if (File.Exists(dest + ".part")) File.Delete(dest + ".part"); }
    }

    [Fact]
    public async Task DownloadVerified_EmptyHash_SkipsCheck_AndWrites()
    {
        var body = new byte[] { 1, 2, 3, 4 };
        var dest = TempPath(".bin");
        using var http = new HttpClient(new BytesHandler(body));
        try
        {
            await DownloadVerifier.DownloadVerifiedAsync(http, "https://x/y", dest, "");
            Assert.True(File.Exists(dest));
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public void ExtractZipSafe_RejectsZipSlipEntry()
    {
        var zipPath = TempPath(".zip");
        var outDir = TempPath("");
        try
        {
            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("..\\escape.txt");
                using var w = new StreamWriter(e.Open());
                w.Write("pwned");
            }
            Assert.Throws<IntegrityException>(() => DownloadVerifier.ExtractZipSafe(zipPath, outDir, overwrite: true));
            // nothing escaped to the parent
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(outDir)!, "escape.txt")));
        }
        finally
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void ExtractZipSafe_NormalEntries_Extract()
    {
        var zipPath = TempPath(".zip");
        var outDir = TempPath("");
        try
        {
            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                using var w = new StreamWriter(zip.CreateEntry("sub/ok.txt").Open());
                w.Write("hello");
            }
            DownloadVerifier.ExtractZipSafe(zipPath, outDir, overwrite: true);
            Assert.True(File.Exists(Path.Combine(outDir, "sub", "ok.txt")));
        }
        finally
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void VerifyAuthenticode_UnsignedFile_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows()) return;
        var f = TempPath(".exe");
        File.WriteAllBytes(f, new byte[] { 0x4D, 0x5A, 0, 0, 0, 0 });  // 'MZ' but no real signature
        try { Assert.False(DownloadVerifier.VerifyAuthenticode(f)); }
        finally { File.Delete(f); }
    }

    [Fact]
    public void VerifyAuthenticode_RealSignedExe_ReturnsTrue()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Validate the P/Invoke against a genuinely signed binary: KiCad's kicad-cli.exe (embedded-signed).
        var kicad = KiCadInstaller.Locate();
        if (kicad is null || !File.Exists(kicad.KicadCliPath)) return;  // skip when KiCad absent
        Assert.True(DownloadVerifier.VerifyAuthenticode(kicad.KicadCliPath),
            "WinVerifyTrust should validate KiCad's embedded Authenticode signature");
    }
}
