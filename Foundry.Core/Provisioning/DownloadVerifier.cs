using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;

namespace Foundry.Core.Provisioning;

/// <summary>Thrown when a download fails an integrity check (SHA-256 mismatch, failed Authenticode, or a
/// zip entry that escapes the extraction directory). A normal <see cref="Exception"/> so the installers'
/// existing catch(Exception)/Status surfaces it.</summary>
public sealed class IntegrityException : Exception
{
    public IntegrityException(string message) : base(message) { }
}

/// <summary>
/// One shared integrity surface for the on-demand toolchain installers, so the same hardening isn't
/// re-inlined (and re-bugged) across five files. Streaming download with optional pinned-SHA-256
/// verification (fail-closed), publisher Authenticode verification for signed executables, and
/// zip-slip-safe extraction. Windows-only Authenticode P/Invoke; the rest is portable.
/// </summary>
public static class DownloadVerifier
{
    /// <summary>
    /// Stream <paramref name="url"/> to <paramref name="destPath"/> (via a <c>.part</c> temp), hashing as we
    /// write. When <paramref name="expectedSha256Hex"/> is non-empty and the computed hash differs, the
    /// partial file is deleted and an <see cref="IntegrityException"/> is thrown (fail-closed). An empty
    /// expected hash skips the SHA check (used for signed artifacts verified by Authenticode after extract).
    /// </summary>
    public static async Task DownloadVerifiedAsync(HttpClient http, string url, string destPath,
        string? expectedSha256Hex, CancellationToken ct = default)
    {
        var part = destPath + ".part";
        try
        {
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                using var src = await resp.Content.ReadAsStreamAsync(ct);
                using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    if (!string.IsNullOrEmpty(expectedSha256Hex)) hash.AppendData(buf, 0, n);
                }
                if (!string.IsNullOrEmpty(expectedSha256Hex))
                {
                    var actual = Convert.ToHexString(hash.GetHashAndReset());
                    if (!actual.Equals(expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
                        throw new IntegrityException(
                            $"{Path.GetFileName(destPath)}: SHA-256 mismatch (expected {expectedSha256Hex}, got {actual}) — refusing to use download.");
                }
            }
            File.Move(part, destPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            throw;
        }
    }

    /// <summary>Verify a file on disk against a pinned SHA-256, throwing <see cref="IntegrityException"/> on mismatch.</summary>
    public static void VerifyFileSha256(string path, string expectedSha256Hex)
    {
        using var fs = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(fs));
        if (!actual.Equals(expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
            throw new IntegrityException(
                $"{Path.GetFileName(path)}: SHA-256 mismatch (expected {expectedSha256Hex}, got {actual}).");
    }

    /// <summary>True if <paramref name="path"/> carries a valid, trusted Authenticode signature (embedded).
    /// Non-Windows or any failure ⇒ false (callers fail-closed where a signature is required).</summary>
    public static bool VerifyAuthenticode(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = Marshal.StringToCoTaskMemUni(path),
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };
        IntPtr pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        IntPtr pData = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_SAFER_FLAG,
            };
            pData = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_DATA>());
            Marshal.StructureToPtr(data, pData, false);

            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, pData);

            // Close the verification state regardless of the result.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(data, pData, true);
            WinVerifyTrust(IntPtr.Zero, ref action, pData);

            return result == 0;  // S_OK
        }
        catch { return false; }
        finally
        {
            if (fileInfo.pcwszFilePath != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);
            if (pFile != IntPtr.Zero) Marshal.FreeCoTaskMem(pFile);
            if (pData != IntPtr.Zero) Marshal.FreeCoTaskMem(pData);
        }
    }

    /// <summary>Return the embedded Authenticode signer certificate, or null for unsigned/unreadable files.</summary>
    public static X509Certificate2? SignerCert(string path)
    {
        try { return new X509Certificate2(X509Certificate.CreateFromSignedFile(path)); }
        catch { return null; }
    }

    /// <summary>Pure helper for publisher allow-lists. A match means the certificate subject contains one of
    /// the expected vendor tokens (case-insensitive). Keep tokens broad enough to survive CA wording changes.</summary>
    public static bool SignerSubjectAllowed(string? subject, params string[] expectedSubjectTokens) =>
        expectedSubjectTokens.Length == 0 ||
        (!string.IsNullOrWhiteSpace(subject) &&
         expectedSubjectTokens.Any(t => !string.IsNullOrWhiteSpace(t) &&
             subject.Contains(t, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Verify <paramref name="path"/> is Authenticode-signed and, when tokens are supplied, signed by
    /// the expected publisher family. Throws <see cref="IntegrityException"/> on failure.</summary>
    public static void RequireAuthenticode(string path, string what, params string[] expectedSubjectTokens)
    {
        if (!VerifyAuthenticode(path))
            throw new IntegrityException($"{what}: failed Authenticode signature verification — refusing to use it.");
        if (expectedSubjectTokens.Length == 0) return;

        var subject = SignerCert(path)?.Subject;
        if (!SignerSubjectAllowed(subject, expectedSubjectTokens))
            throw new IntegrityException(
                $"{what}: signer '{subject ?? "unknown"}' did not match expected publisher ({string.Join(" / ", expectedSubjectTokens)}) — refusing to use it.");
    }

    /// <summary>
    /// QUARANTINE-THEN-PROMOTE: extract into a private staging dir, verify it there, and only move it into
    /// <paramref name="targetDir"/> once verification passes. Nothing observable is created until the payload
    /// has been checked — on failure the staging tree is deleted and the exception propagates.
    /// <para>
    /// Extracting straight into the live tools dir and verifying afterwards is not equivalent: the rejected
    /// payload survives the throw, and since every installer's <c>Locate()</c> is a file-existence check (and
    /// <c>ToolchainProvisioner.InstallAsync</c> short-circuits on <c>IsInstalled</c>), the binary that FAILED
    /// verification is then reported as installed and executed on every subsequent run.
    /// </para>
    /// </summary>
    public static void ExtractVerifiedZip(string zipPath, string targetDir, Action<string>? verifyStaged = null)
    {
        var staging = StagingPathFor(targetDir);
        try
        {
            ExtractZipSafe(zipPath, staging, overwrite: true);
            verifyStaged?.Invoke(staging);
            PromoteDirectory(staging, targetDir);
        }
        finally { TryDelete(staging); }
    }

    /// <summary>
    /// The single-file form of <see cref="ExtractVerifiedZip"/>, for archives whose payload is one executable
    /// landing in a SHARED directory (promoting the whole directory there would clobber sibling tools).
    /// Extracts to staging, locates <paramref name="entryFileName"/> anywhere inside it, verifies, then moves
    /// just that file to <paramref name="destPath"/>.
    /// </summary>
    public static void ExtractVerifiedFile(string zipPath, string entryFileName, string destPath,
        Action<string>? verifyStaged = null)
    {
        var staging = StagingPathFor(destPath);
        try
        {
            ExtractZipSafe(zipPath, staging, overwrite: true);
            var staged = Directory.EnumerateFiles(staging, entryFileName, SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new IntegrityException($"{entryFileName} not found in {Path.GetFileName(zipPath)}.");
            verifyStaged?.Invoke(staged);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Move(staged, destPath, overwrite: true);
        }
        finally { TryDelete(staging); }
    }

    private static string StagingPathFor(string target) =>
        target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + ".staging-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Swap a verified staging dir into place, retiring (and only then deleting) any previous
    /// install — so a failed move leaves the working tool where it was rather than uninstalling it.</summary>
    private static void PromoteDirectory(string staging, string targetDir)
    {
        var parent = Path.GetDirectoryName(targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        string? retired = null;
        if (Directory.Exists(targetDir))
        {
            retired = targetDir + ".old-" + Guid.NewGuid().ToString("N")[..8];
            Directory.Move(targetDir, retired);
        }
        try { Directory.Move(staging, targetDir); }
        catch
        {
            if (retired is not null && !Directory.Exists(targetDir)) Directory.Move(retired, targetDir);
            throw;
        }
        if (retired is not null) TryDelete(retired);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Zip-slip-safe extraction: every entry must resolve INSIDE <paramref name="targetDir"/>, else
    /// <see cref="IntegrityException"/>. Replaces <see cref="ZipFile.ExtractToDirectory(string,string)"/>,
    /// which has no path-traversal guard.</summary>
    public static void ExtractZipSafe(string zipPath, string targetDir, bool overwrite)
    {
        Directory.CreateDirectory(targetDir);
        var root = Path.GetFullPath(targetDir + Path.DirectorySeparatorChar);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var full = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new IntegrityException($"zip entry escapes target dir (zip-slip): {entry.FullName}");
            if (entry.Name.Length == 0) continue;  // directory entry
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            entry.ExtractToFile(full, overwrite);
        }
    }

    // ---- WinVerifyTrust P/Invoke ---------------------------------------------------------------------
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x100;
    private static Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);
}
