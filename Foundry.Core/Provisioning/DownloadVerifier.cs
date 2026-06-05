using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.IO.Compression;

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

    /// <summary>Verify <paramref name="path"/> is Authenticode-signed; throw <see cref="IntegrityException"/>
    /// (fail-closed) if not. Use for downloaded executables from publishers that embed-sign their binaries.</summary>
    public static void RequireAuthenticode(string path, string what)
    {
        if (!VerifyAuthenticode(path))
            throw new IntegrityException($"{what}: failed Authenticode signature verification — refusing to use it.");
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
