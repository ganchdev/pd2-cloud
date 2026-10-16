using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PD2Cloud.GitHub;

namespace PD2Cloud.Platform;

// No file, environment-token or in-memory persistence fallback. Native failures propagate.
public sealed class NativeCredentialStore(string clientId) : ICredentialStore
{
    private readonly string key = "PD2Cloud.GitHub." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId)))[..24];
    public Task<string?> ReadAsync(CancellationToken ct) => RunAsync(() => Read(), ct);
    public Task WriteAsync(string secret, CancellationToken ct) => RunAsync(() => { Write(secret); return true; }, ct);
    public Task DeleteAsync(CancellationToken ct) => RunAsync(() => { Delete(); return true; }, ct);

    private static Task<T> RunAsync<T>(Func<T> action, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        return action(); // Native mutations finish before the connection gate is released.
    }, ct);

    private string? Read()
    {
        if (OperatingSystem.IsMacOS()) return MacRead();
        if (OperatingSystem.IsWindows())
        {
            if (!CredRead(key, 1, 0, out var pointer))
            {
                if (Marshal.GetLastWin32Error() == 1168) return null;
                throw new Win32Exception();
            }
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                var bytes = new byte[credential.BlobSize];
                Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
                try { return Encoding.UTF8.GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally { CredFree(pointer); }
        }
        return Linux("lookup", null);
    }

    private void Write(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            if (OperatingSystem.IsMacOS()) { MacWrite(bytes); return; }
            if (OperatingSystem.IsWindows())
            {
                if (bytes.Length > 2560) throw new InvalidOperationException("Credential exceeds Windows storage limit.");
                var pointer = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, pointer, bytes.Length);
                    var credential = new Credential { Type = 1, TargetName = key, BlobSize = (uint)bytes.Length, Blob = pointer, Persist = 2, UserName = "GitHub" };
                    if (!CredWrite(ref credential, 0)) throw new Win32Exception();
                }
                finally
                {
                    for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(pointer, i, 0);
                    Marshal.FreeHGlobal(pointer);
                }
                return;
            }
            _ = Linux("store", secret);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void Delete()
    {
        if (OperatingSystem.IsMacOS())
        {
            var status = MacFind(out _, out var data, out var item);
            if (status == -25300) return;
            CheckMac(status);
            try { CheckMac(SecKeychainItemDelete(item)); }
            finally { SecKeychainItemFreeContent(IntPtr.Zero, data); CFRelease(item); }
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            if (!CredDelete(key, 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new Win32Exception();
            return;
        }
        _ = Linux("clear", null);
    }

    private string? Linux(string operation, string? secret)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Native credential facility unavailable.");
        // libsecret's CLI communicates with the native Secret Service. Secrets go over stdin, never argv.
        var start = new ProcessStartInfo("secret-tool") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(operation);
        if (operation == "store") start.ArgumentList.Add("--label=PD2 Cloud GitHub");
        start.ArgumentList.Add("application"); start.ArgumentList.Add(key);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Secret Service unavailable.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (secret is not null) process.StandardInput.Write(secret);
        process.StandardInput.Close();
        if (!process.WaitForExit(30000)) { process.Kill(); throw new TimeoutException("Native credential facility did not respond."); }
        var stderr = error.GetAwaiter().GetResult();
        var stdout = output.GetAwaiter().GetResult();
        if (process.ExitCode == 1 && stderr.Length == 0 && operation is "lookup" or "clear") return null;
        if (process.ExitCode != 0 || stderr.Length != 0) throw new InvalidOperationException("Native credential operation failed.");
        return operation == "lookup" ? stdout.TrimEnd('\r', '\n') : null;
    }

    private int MacFind(out uint length, out IntPtr data, out IntPtr item) =>
        SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Encoding.UTF8.GetByteCount(key), key, 6, "GitHub", out length, out data, out item);
    private string? MacRead()
    {
        var status = MacFind(out var length, out var data, out var item);
        if (status == -25300) return null;
        CheckMac(status);
        try
        {
            var bytes = new byte[length]; Marshal.Copy(data, bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { SecKeychainItemFreeContent(IntPtr.Zero, data); CFRelease(item); }
    }
    private void MacWrite(byte[] bytes)
    {
        var status = MacFind(out _, out var data, out var item);
        if (status == -25300)
        {
            CheckMac(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Encoding.UTF8.GetByteCount(key), key, 6, "GitHub", (uint)bytes.Length, bytes, out item));
            CFRelease(item);
            return;
        }
        CheckMac(status);
        try { CheckMac(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)bytes.Length, bytes)); }
        finally { SecKeychainItemFreeContent(IntPtr.Zero, data); CFRelease(item); }
    }
    private static void CheckMac(int status)
    {
        if (status != 0) throw new InvalidOperationException($"Keychain operation failed ({status}).");
    }

    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    [DllImport(Security)] private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string service, uint accountLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string account, out uint length, out IntPtr data, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string service, uint accountLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string account, uint length, byte[] data, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport(Security)] private static extern int SecKeychainItemDelete(IntPtr item);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr value);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
}
