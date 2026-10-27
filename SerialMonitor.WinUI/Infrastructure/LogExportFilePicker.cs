using System.Runtime.InteropServices;

namespace SerialMonitor.WinUI.Infrastructure;

// Select a destination without creating it. The WinRT save picker creates an
// empty StorageFile before export, leaving a misleading file behind on Cancel.
internal static class LogExportFilePicker
{
    private const int Canceled = unchecked((int)0x800704C7);
    private const uint OverwritePrompt = 0x2;
    private const uint NoChangeDirectory = 0x8;
    private const uint ForceFileSystem = 0x40;
    private const uint PathMustExist = 0x800;
    private const uint DoNotTestFileCreation = 0x10000;
    private const uint FileSystemPath = 0x80058000;
    private static readonly Guid SaveDialogClass = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
    private static readonly Guid PickerSettings = new("839E85B8-AB36-4DFC-B6B5-A5BF53E8E073");

    public static Task<string?> PickAsync(nint owner, string suggestedName, string title, string fileType, string commitText)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The native dialog owns an STA/message loop; the WinUI dispatcher remains
        // free to render RX and process background-service notifications.
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(Pick(owner, suggestedName, title, fileType, commitText)); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "Log export destination picker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static string? Pick(nint owner, string suggestedName, string title, string fileType, string commitText)
    {
        IFileDialog? dialog = null;
        IShellItem? item = null;
        try
        {
            dialog = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(SaveDialogClass, throwOnError: true)!)!;
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | OverwritePrompt | NoChangeDirectory | ForceFileSystem | PathMustExist | DoNotTestFileCreation);
            dialog.SetFileTypes(1, [new FilterSpec { Name = fileType, Pattern = "*.log;*.txt" }]);
            dialog.SetDefaultExtension("log");
            dialog.SetFileName(suggestedName);
            dialog.SetTitle(title);
            dialog.SetOkButtonLabel(commitText);
            var settings = PickerSettings;
            dialog.SetClientGuid(ref settings);
            var result = dialog.Show(owner);
            if (result == Canceled) return null;
            Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out item);
            item.GetDisplayName(FileSystemPath, out var path);
            return path;
        }
        finally
        {
            if (item is not null) Marshal.ReleaseComObject(item);
            if (dialog is not null) Marshal.ReleaseComObject(dialog);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Pattern;
    }

    // Declaration order follows IModalWindow/IFileDialog in Windows SDK
    // ShObjIdl_core.h. Unused methods retain their slots in the native vtable.
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(nint owner);
        void SetFileTypes(uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(nint events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(nint item);
        void SetFolder(nint item);
        void GetFolder(out nint item);
        void GetCurrentSelection(out nint item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out nint name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetResult(out IShellItem item);
        void AddPlace(nint item, uint location);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(nint filter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint context, ref Guid handler, ref Guid iid, out nint result);
        void GetParent(out nint parent);
        void GetDisplayName(uint format, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(nint item, uint hint, out int order);
    }
}
