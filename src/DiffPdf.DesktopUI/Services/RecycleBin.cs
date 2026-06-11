using System.IO;
using System.Runtime.InteropServices;

namespace DiffPdf.DesktopUI.Services;

/// <summary>
/// Sends a file or folder (recursively) to the Windows Recycle Bin via <c>SHFileOperation</c> with
/// <c>FOF_ALLOWUNDO</c> — the only supported way to recycle from .NET without the VB runtime.
/// Volumes without a bin (network shares, some removable media) delete permanently, exactly like
/// Explorer does. Windows-only, like the rest of the app.
/// </summary>
internal static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;        // recycle instead of delete
    private const ushort FOF_NOCONFIRMATION = 0x0010;   // no shell "are you sure" — the app already asked
    private const ushort FOF_SILENT = 0x0004;           // no shell progress window
    private const ushort FOF_NOERRORUI = 0x0400;        // errors come back as a code, not a dialog

    public static void Delete(string absolutePath)
    {
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = absolutePath + "\0", // the marshalled string adds the second terminator of the double-null list
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        int result = SHFileOperation(ref operation);
        if (result != 0 || operation.fAnyOperationsAborted)
            throw new IOException($"Přesun do koše selhal (kód {result}): {absolutePath}");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    // The 64-bit (unpacked) layout — the app is x64-only, matching the rest of the Windows-only build.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }
}
