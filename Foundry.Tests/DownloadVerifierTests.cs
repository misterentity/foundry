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

    [Theory]
    [InlineData("CN=Arduino SA, O=Arduino SA, L=Turin", "Arduino", true)]
    [InlineData("CN=Eclipse Adoptium, O=Eclipse Foundation", "Adoptium", true)]
    [InlineData("CN=Unexpected Vendor, O=Other", "Arduino", false)]
    [InlineData("", "Arduino", false)]
    public void SignerSubjectAllowed_MatchesExpectedPublisherToken(string subject, string token, bool expected)
    {
        Assert.Equal(expected, DownloadVerifier.SignerSubjectAllowed(subject, token));
    }

    // ---- quarantine-then-promote ------------------------------------------------------------------
    //
    // The property under test is the one whose absence let a REJECTED binary stay installed: extracting
    // into the live tools dir and verifying afterwards leaves the payload on disk when verification
    // throws, and every installer's Locate() is a bare file-existence check — so the tool that failed
    // its integrity check is reported installed and executed on every later run.

    private static string MakeZip(params (string Path, string Body)[] entries)
    {
        var zipPath = TempPath(".zip");
        using var fs = new FileStream(zipPath, FileMode.Create);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (path, body) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(path).Open());
            w.Write(body);
        }
        return zipPath;
    }

    [Fact]
    public void ExtractVerifiedZip_PromotesPayload_WhenVerificationPasses()
    {
        var zipPath = MakeZip(("tool/bin/app.exe", "payload"));
        var target = TempPath("");
        try
        {
            DownloadVerifier.ExtractVerifiedZip(zipPath, target, staged =>
                Assert.True(Directory.EnumerateFiles(staged, "app.exe", SearchOption.AllDirectories).Any()));

            Assert.True(File.Exists(Path.Combine(target, "tool", "bin", "app.exe")));
        }
        finally { File.Delete(zipPath); if (Directory.Exists(target)) Directory.Delete(target, true); }
    }

    [Fact]
    public void ExtractVerifiedZip_LeavesNothingOnDisk_WhenVerificationFails()
    {
        var zipPath = MakeZip(("tool/bin/app.exe", "payload"));
        var target = TempPath("");
        try
        {
            Assert.Throws<IntegrityException>(() =>
                DownloadVerifier.ExtractVerifiedZip(zipPath, target, _ => throw new IntegrityException("bad signature")));

            Assert.False(Directory.Exists(target), "a rejected payload must not be promoted");
            // ...and no staging tree may survive for Locate() to stumble onto.
            var siblings = Directory.EnumerateDirectories(Path.GetDirectoryName(target)!,
                Path.GetFileName(target) + ".staging-*");
            Assert.Empty(siblings);
        }
        finally { File.Delete(zipPath); if (Directory.Exists(target)) Directory.Delete(target, true); }
    }

    [Fact]
    public void ExtractVerifiedZip_KeepsTheWorkingInstall_WhenAReinstallFailsVerification()
    {
        var zipPath = MakeZip(("tool/app.exe", "new"));
        var target = TempPath("");
        try
        {
            Directory.CreateDirectory(Path.Combine(target, "tool"));
            File.WriteAllText(Path.Combine(target, "tool", "app.exe"), "existing-good-install");

            Assert.Throws<IntegrityException>(() =>
                DownloadVerifier.ExtractVerifiedZip(zipPath, target, _ => throw new IntegrityException("bad hash")));

            // A failed upgrade must not uninstall the tool that was already working.
            Assert.Equal("existing-good-install", File.ReadAllText(Path.Combine(target, "tool", "app.exe")));
        }
        finally { File.Delete(zipPath); if (Directory.Exists(target)) Directory.Delete(target, true); }
    }

    [Fact]
    public void ExtractVerifiedFile_PromotesOnlyTheNamedFile()
    {
        var zipPath = MakeZip(("nested/arduino-cli.exe", "cli"), ("nested/readme.txt", "docs"));
        var dest = TempPath(".exe");
        try
        {
            DownloadVerifier.ExtractVerifiedFile(zipPath, "arduino-cli.exe", dest);

            Assert.Equal("cli", File.ReadAllText(dest));
            // The shared tools dir must not be littered with the archive's other entries.
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dest)!, "readme.txt")));
        }
        finally { File.Delete(zipPath); if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public void ExtractVerifiedFile_DoesNotWriteDestination_WhenVerificationFails()
    {
        var zipPath = MakeZip(("arduino-cli.exe", "cli"));
        var dest = TempPath(".exe");
        try
        {
            Assert.Throws<IntegrityException>(() =>
                DownloadVerifier.ExtractVerifiedFile(zipPath, "arduino-cli.exe", dest,
                    _ => throw new IntegrityException("unsigned")));

            Assert.False(File.Exists(dest));
        }
        finally { File.Delete(zipPath); if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public void ExtractVerifiedFile_ThrowsWhenTheNamedFileIsAbsent()
    {
        var zipPath = MakeZip(("something-else.txt", "x"));
        var dest = TempPath(".exe");
        try
        {
            Assert.Throws<IntegrityException>(() =>
                DownloadVerifier.ExtractVerifiedFile(zipPath, "arduino-cli.exe", dest));
            Assert.False(File.Exists(dest));
        }
        finally { File.Delete(zipPath); if (File.Exists(dest)) File.Delete(dest); }
    }

    // Guard the provenance constants: a blank or malformed pin silently disables the ONLY integrity anchor
    // these two tools have (neither publisher Authenticode-signs its binaries).
    [Theory]
    [InlineData("openscad")]
    [InlineData("renode")]
    public void UnsignedTools_KeepAPinnedArchiveHash(string tool)
    {
        var pin = tool == "openscad"
            ? Foundry.Core.Cad.OpenScadInstaller.PortableSha256
            : Foundry.Core.Simulation.RenodeInstaller.PortableSha256;
        Assert.Matches("^[A-F0-9]{64}$", pin);
    }
}
