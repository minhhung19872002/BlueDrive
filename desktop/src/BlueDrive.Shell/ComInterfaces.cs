using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace BlueDrive.Shell;

// Shell COM interfaces used by the Windows 11 context menu (shobjidl_core.h), in vtable order.

[GeneratedComInterface, Guid("00000001-0000-0000-C000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(nint outer, in Guid iid, out nint obj);

    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}

[GeneratedComInterface, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    [PreserveSig] int BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);
    [PreserveSig] int GetParent(out nint ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName, out nint ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(nint psi, uint hint, out int piOrder);
}

[GeneratedComInterface, Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
internal partial interface IShellItemArray
{
    [PreserveSig] int BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppvOut);
    [PreserveSig] int GetPropertyStore(int flags, in Guid riid, out nint ppv);
    [PreserveSig] int GetPropertyDescriptionList(nint keyType, in Guid riid, out nint ppv);
    [PreserveSig] int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int GetCount(out uint pdwNumItems);
    [PreserveSig] int GetItemAt(uint dwIndex, out IShellItem ppsi);
    [PreserveSig] int EnumItems(out nint ppenumShellItems);
}

[GeneratedComInterface, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9")]
internal partial interface IExplorerCommand
{
    [PreserveSig] int GetTitle(IShellItemArray? psiItemArray, out nint ppszName);
    [PreserveSig] int GetIcon(IShellItemArray? psiItemArray, out nint ppszIcon);
    [PreserveSig] int GetToolTip(IShellItemArray? psiItemArray, out nint ppszInfotip);
    [PreserveSig] int GetCanonicalName(out Guid pguidCommandName);
    [PreserveSig] int GetState(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.Bool)] bool fOkToBeSlow, out uint pCmdState);
    [PreserveSig] int Invoke(IShellItemArray? psiItemArray, nint pbc);
    [PreserveSig] int GetFlags(out uint pFlags);
    [PreserveSig] int EnumSubCommands(out IEnumExplorerCommand? ppEnum);
}

[GeneratedComInterface, Guid("a88826f8-186f-4987-aade-ea0cef8fbfe8")]
internal partial interface IEnumExplorerCommand
{
    [PreserveSig] int Next(uint celt, out IExplorerCommand? pUICommand, out uint pceltFetched);
    [PreserveSig] int Skip(uint celt);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumExplorerCommand? ppenum);
}
