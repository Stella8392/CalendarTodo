using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CalendarTodo;

internal static class Credentials
{
    private static string Target(string account) =>
        $"CalendarTodo/{MailProvider.ForAddress(account)?.Id ?? throw new ArgumentException("不支持的邮箱地址")}/{account}";

    public static string? Read(string account)
    {
        if (!CredRead(Target(account), 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == 1168 ? null : throw new Win32Exception(error);
        }
        try
        {
            var item = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(item.Blob, item.BlobSize / 2);
        }
        finally { CredFree(pointer); }
    }

    public static void Save(string account, string password)
    {
        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var item = new Credential
            {
                Type = 1, TargetName = Target(account), UserName = account, Persist = 2,
                BlobSize = Encoding.Unicode.GetByteCount(password), Blob = blob
            };
            if (!CredWrite(ref item, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }

    public static void Delete(string account)
    {
        if (!CredDelete(Target(account), 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? Alias, UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr pointer);
}
