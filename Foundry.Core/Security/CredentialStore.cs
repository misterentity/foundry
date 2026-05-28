using System.Runtime.InteropServices;
using System.Text;

namespace Foundry.Core.Security;

/// <summary>
/// Stores API keys in Windows Credential Manager (DPAPI-backed). Keys are never
/// written to the Project file or logs (PRD §8.9, §14). After save the UI only ever
/// shows a masked summary via <see cref="Mask"/>.
/// </summary>
public sealed class CredentialStore : ICredentialStore
{
    public const string AnthropicTarget = "Foundry:Anthropic";
    public const string NexarTarget     = "Foundry:Nexar";
    public const string DigiKeyTarget   = "Foundry:DigiKey";
    public const string MouserTarget    = "Foundry:Mouser";
    public const string PcbWayTarget     = "Foundry:PcbWay";
    public const string JlcpcbTarget     = "Foundry:Jlcpcb";

    public void Save(string target, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var cred = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = target,
            CredentialBlobSize = (uint)blob.Length,
            CredentialBlob = Marshal.AllocCoTaskMem(blob.Length),
            Persist = CRED_PERSIST_LOCAL_MACHINE,
            UserName = "foundry",
        };
        try
        {
            Marshal.Copy(blob, 0, cred.CredentialBlob, blob.Length);
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException($"CredWrite failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            if (cred.CredentialBlob != IntPtr.Zero) Marshal.FreeCoTaskMem(cred.CredentialBlob);
        }
    }

    public string? Read(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var handle))
            return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return "";
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
            return Encoding.Unicode.GetString(bytes);
        }
        finally { CredFree(handle); }
    }

    public void Delete(string target) => CredDelete(target, CRED_TYPE_GENERIC, 0);

    public bool Exists(string target) => !string.IsNullOrEmpty(Read(target));

    /// <summary>Masked summary for display, e.g. "sk-ant-…AB12". Never reveals the full key.</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "—";
        if (secret.Length <= 8) return new string('•', secret.Length);
        var head = secret.Length >= 7 ? secret[..7] : secret[..4];
        var tail = secret[^4..];
        return $"{head}…{tail}";
    }

    // ---- P/Invoke ----
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}

/// <summary>Abstraction so view models can be tested without touching the OS store.</summary>
public interface ICredentialStore
{
    void Save(string target, string secret);
    string? Read(string target);
    void Delete(string target);
    bool Exists(string target);
}
