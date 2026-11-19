using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CalendarTodo.Mac;

internal static class Keychain
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly IntPtr SecurityLibrary = NativeLibrary.Load(Security);
    private static readonly IntPtr FoundationLibrary = NativeLibrary.Load(Foundation);
    private static IntPtr Symbol(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityLibrary, name));

    private sealed class Dictionary : IDisposable
    {
        public IntPtr Handle { get; } = CFDictionaryCreateMutable(IntPtr.Zero, 0,
            NativeLibrary.GetExport(FoundationLibrary, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(FoundationLibrary, "kCFTypeDictionaryValueCallBacks"));

        public void Set(string key, IntPtr value) => CFDictionarySetValue(Handle, Symbol(key), value);
        public void Text(string key, string value)
        {
            var text = CFStringCreateWithCString(IntPtr.Zero, value, 0x08000100);
            try { Set(key, text); } finally { CFRelease(text); }
        }
        public void Dispose() => CFRelease(Handle);
    }

    private static Dictionary Query(string account)
    {
        var query = new Dictionary();
        query.Set("kSecClass", Symbol("kSecClassGenericPassword"));
        query.Text("kSecAttrService", "CalendarTodo/" + (MailProvider.ForAddress(account)?.Id ?? throw new ArgumentException("不支持的邮箱")));
        query.Text("kSecAttrAccount", account);
        return query;
    }

    public static string? Read(string account)
    {
        using var query = Query(account);
        query.Set("kSecReturnData", Marshal.ReadIntPtr(NativeLibrary.GetExport(FoundationLibrary, "kCFBooleanTrue")));
        var status = SecItemCopyMatching(query.Handle, out var data);
        if (status == -25300) return null;
        Check(status);
        try
        {
            var bytes = new byte[checked((int)CFDataGetLength(data))];
            Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CFRelease(data); }
    }

    public static void Save(string account, string secret)
    {
        using var query = Query(account);
        using var values = new Dictionary();
        var bytes = Encoding.UTF8.GetBytes(secret);
        var data = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
        CryptographicOperations.ZeroMemory(bytes);
        try
        {
            values.Set("kSecValueData", data);
            var status = SecItemUpdate(query.Handle, values.Handle);
            if (status == -25300)
            {
                query.Set("kSecValueData", data);
                status = SecItemAdd(query.Handle, IntPtr.Zero);
            }
            Check(status);
        }
        finally { CFRelease(data); }
    }

    public static void Delete(string account)
    {
        using var query = Query(account);
        var status = SecItemDelete(query.Handle);
        if (status != -25300) Check(status);
    }

    private static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException($"macOS 钥匙串访问失败（{status}）");
    }

    [DllImport(Security)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Security)] private static extern int SecItemAdd(IntPtr query, IntPtr result);
    [DllImport(Security)] private static extern int SecItemUpdate(IntPtr query, IntPtr values);
    [DllImport(Security)] private static extern int SecItemDelete(IntPtr query);
    [DllImport(Foundation)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(Foundation)] private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);
    [DllImport(Foundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(Foundation)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
    [DllImport(Foundation)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(Foundation)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(Foundation)] private static extern void CFRelease(IntPtr item);
}
