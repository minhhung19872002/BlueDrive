using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BlueDrive.Sync.Client;

namespace BlueDrive.Sync.Agent;

/// <summary>
/// macOS: device tokens in the user's login Keychain (generic password, service "BlueDrive", one item per account), like DPAPI on
/// Windows: other users cannot read them and they never sit in a plain file. Security.framework's keychain item API.
/// </summary>
public sealed class KeychainTokenStore(string account) : ITokenStore
{
    public const string Service = "BlueDrive";
    private const int ErrSecItemNotFound = -25300;
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private static readonly byte[] ServiceBytes = Encoding.UTF8.GetBytes(Service);
    private readonly byte[] _account = Encoding.UTF8.GetBytes(account);

    public DeviceTokens? Load()
    {
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes, (uint)_account.Length, _account,
            out var length, out var data, out var item);
        if (status == ErrSecItemNotFound) return null;
        Check(status, "đọc");
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
            return JsonSerializer.Deserialize<StoredTokens>(bytes)?.ToTokens();
        }
        catch (JsonException) { return null; }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            CFRelease(item);
        }
    }

    public void Save(DeviceTokens tokens)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(StoredTokens.From(tokens));
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes, (uint)_account.Length, _account,
            out _, IntPtr.Zero, out var item);
        if (status == 0)
        {
            try { Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)bytes.Length, bytes), "ghi"); }
            finally { CFRelease(item); }
            return;
        }
        if (status != ErrSecItemNotFound) Check(status, "ghi");
        Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes, (uint)_account.Length, _account,
            (uint)bytes.Length, bytes, out var added), "ghi");
        CFRelease(added);
    }

    public void Clear()
    {
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes, (uint)_account.Length, _account,
            out _, IntPtr.Zero, out var item);
        if (status == ErrSecItemNotFound) return;
        Check(status, "xóa");
        try { Check(SecKeychainItemDelete(item), "xóa"); }
        finally { CFRelease(item); }
    }

    private static void Check(int status, string what)
    {
        if (status != 0) throw new IOException($"Không {what} được thông tin đăng nhập trong Keychain (mã {status}).");
    }

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength,
        byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength,
        byte[] accountName, out uint passwordLength, IntPtr passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
        uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}
