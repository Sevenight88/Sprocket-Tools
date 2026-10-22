using System.Runtime.InteropServices;

namespace SprocketTools;

/// Native OBJ dialogs. Call outside an inspector callback; the picker owns no Unity objects or files.
internal static class ObjFilePicker
{
    const int MaxPathCharacters = 32768;
    const int Cancelled = unchecked((int)0x800704C7);
    const uint OverwritePrompt = 0x2, StrictFileTypes = 0x4, NoChangeDirectory = 0x8,
        ForceFileSystem = 0x40, PathMustExist = 0x800, FileMustExist = 0x1000, NoTestFileCreate = 0x10000;
    static readonly Guid OpenDialogClass = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    static readonly Guid SaveDialogClass = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
    static readonly Guid FileDialogId = new("42F85136-DB7E-439C-85F1-E4075D135FC8");
    static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public static Task<string?> OpenAsync(string? initialDirectory) => PickAsync(initialDirectory, null, false);
    public static Task<string?> SaveAsync(string? initialDirectory, string suggestedName) => PickAsync(initialDirectory, suggestedName, true);

    static Task<string?> PickAsync(string? initialDirectory, string? suggestedName, bool save)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OBJ 文件对话框只支持 Windows。");
            IntPtr owner = GetActiveWindow(); // Capture the game window on its own thread.
            // Unity's main thread may already be MTA. The Windows dialog needs a separate STA and
            // creates its own message loop; no game code runs on this thread. Do not join here:
            // an owned modal dialog sends messages to the game, which must keep pumping frames.
            var thread = new Thread(() =>
            {
                try { completion.TrySetResult(PickWindows(owner, initialDirectory, suggestedName, save)); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }) { IsBackground = true, Name = "Sprocket OBJ file picker" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        return completion.Task;
    }

    static string? PickWindows(IntPtr owner, string? initialDirectory, string? suggestedName, bool save)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OBJ 文件对话框只支持 Windows。");
        int initialized = CoInitializeEx(IntPtr.Zero, 0x2); // COINIT_APARTMENTTHREADED
        Marshal.ThrowExceptionForHR(initialized);
        IFileDialog? dialog = null;
        try
        {
            Guid classId = save ? SaveDialogClass : OpenDialogClass, interfaceId = FileDialogId;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, IntPtr.Zero, 1, ref interfaceId, out dialog));
            dialog.GetOptions(out uint options);
            options |= NoChangeDirectory | ForceFileSystem | PathMustExist | StrictFileTypes;
            // The dialog selects a path. It never tests by creating a file, and existing exports
            // are rejected below. The export writer still uses CreateNew to close the race.
            if (save) options = (options | NoTestFileCreate) & ~OverwritePrompt;
            else options |= FileMustExist;
            dialog.SetOptions(options);
            dialog.SetFileTypes(1, new[] { new FilterSpec { Name = "Wavefront OBJ 模型 (*.obj)", Pattern = "*.obj" } });
            dialog.SetFileTypeIndex(1);
            dialog.SetDefaultExtension("obj");
            dialog.SetTitle(save ? "把载具导出为 OBJ" : "把 OBJ 导入为板结构");
            dialog.SetOkButtonLabel(save ? "导出" : "导入");
            Guid clientId = new(save ? "034B5132-E8C2-4778-928F-78A69F507B76" : "5D0D0527-2E2A-49A7-B9C6-CA1190344FC2");
            dialog.SetClientGuid(ref clientId);
            if (save) dialog.SetFileName(SuggestedName(suggestedName));

            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                Guid shellId = ShellItemId;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(Path.GetFullPath(initialDirectory), IntPtr.Zero,
                    ref shellId, out IShellItem folder));
                try { dialog.SetDefaultFolder(folder); }
                finally { Marshal.FinalReleaseComObject(folder); }
            }
            while (true)
            {
                int shown = dialog.Show(owner);
                if (shown == Cancelled) return null;
                Marshal.ThrowExceptionForHR(shown);
                dialog.GetResult(out IShellItem item);
                string path;
                try
                {
                    item.GetDisplayName(0x80058000, out IntPtr text); // SIGDN_FILESYSPATH
                    try { path = ReadPath(text); }
                    finally { Marshal.FreeCoTaskMem(text); }
                }
                finally { Marshal.FinalReleaseComObject(item); }
                path = Path.GetFullPath(path);
                if (path.Length >= MaxPathCharacters) throw new PathTooLongException("所选的 OBJ 路径太长。");
                if (!string.Equals(Path.GetExtension(path), ".obj", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBoxW(owner, "请选择扩展名为 .obj 的 Wavefront OBJ 文件。", "OBJ 文件", 0x40);
                    continue;
                }
                if (save && (File.Exists(path) || Directory.Exists(path)))
                {
                    MessageBoxW(owner, "这个名称已经存在。请另起一个名字，好保住已有的文件。", "把载具导出为 OBJ", 0x40);
                    continue;
                }
                if (!save && !File.Exists(path))
                    throw new FileNotFoundException("所选的 OBJ 文件已经不可用了。", path);
                return path;
            }
        }
        finally
        {
            if (dialog != null) Marshal.FinalReleaseComObject(dialog);
            CoUninitialize();
        }
    }

    static string SuggestedName(string? name)
    {
        name = Path.GetFileNameWithoutExtension(name ?? "");
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0) name = "Tank";
        if (name.Length > 240) name = name[..240];
        return name + ".obj";
    }

    static string ReadPath(IntPtr text)
    {
        if (text == IntPtr.Zero) throw new IOException("OBJ 对话框没有返回路径。");
        // Shell allocates the returned Unicode string. Bound the read just as a traditional
        // 32768-character OPENFILENAME buffer would, then free it on every exit path.
        int length = 0;
        while (length < MaxPathCharacters && Marshal.ReadInt16(text, length * 2) != 0) length++;
        if (length == 0) throw new IOException("OBJ 对话框返回了一个空路径。");
        if (length >= MaxPathCharacters) throw new PathTooLongException("所选的 OBJ 路径太长。");
        return Marshal.PtrToStringUni(text, length)!;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Pattern;
    }

    // IModalWindow.Show followed by IFileDialog's complete vtable, in Windows SDK order.
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem selection);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint location);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid interfaceId, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr text);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("user32.dll", ExactSpelling = true)] static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
    [DllImport("ole32.dll", ExactSpelling = true)] static extern int CoInitializeEx(IntPtr reserved, uint concurrency);
    [DllImport("ole32.dll", ExactSpelling = true)] static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)]
    static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IFileDialog dialog);
    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
}
