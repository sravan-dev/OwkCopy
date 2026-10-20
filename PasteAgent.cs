using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OwkCopy;

/// <summary>
/// Background agent that makes Owk Copy the default for Ctrl+V in Explorer and on the desktop.
/// A low-level keyboard hook swallows Ctrl+V only when the file list has focus and the clipboard
/// holds files; anything it cannot resolve (zip folders, This PC, ...) is replayed to Explorer.
/// </summary>
sealed class PasteAgent : IDisposable
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "OwkCopy";
    public const string MutexName = @"Local\OwkCopyAgent";
    public const string QuitEventName = @"Local\OwkCopyAgentQuit";

    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100;
    const int VK_V = 0x56, VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    const uint CF_HDROP = 15;
    static readonly IntPtr Marker = 0x4F574B; // tags our own replayed keystrokes

    readonly Dispatcher _ui;
    readonly HookProc _proc;
    IntPtr _hook;
    public bool Enabled { get; set; } = true;

    public PasteAgent(Dispatcher ui)
    {
        _ui = ui;
        _proc = Hook;
    }

    public void Start() =>
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    // ---------------------------------------------------------------- install state

    static string ExePath => Environment.ProcessPath!;

    public static bool IsDefault()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) is string v && v.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetDefault(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
        {
            k.SetValue(RunValue, $"\"{ExePath}\" --agent");
            if (!IsAgentRunning()) Process.Start(ExePath, "--agent");
        }
        else
        {
            k.DeleteValue(RunValue, false);
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out var quit)) { quit.Set(); quit.Dispose(); }
        }
    }

    static bool IsAgentRunning()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var m)) return false;
        m.Dispose();
        return true;
    }

    // ---------------------------------------------------------------- hook

    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Enabled && wParam == WM_KEYDOWN)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (k.vkCode == VK_V && k.dwExtraInfo != Marker &&
                Down(VK_CONTROL) && !Down(VK_SHIFT) && !Down(VK_MENU) && !Down(VK_LWIN) && !Down(VK_RWIN) &&
                IsClipboardFormatAvailable(CF_HDROP))
            {
                var fg = GetForegroundWindow();
                var cls = ClassOf(fg);
                if (cls is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW" && FileViewHasFocus(fg))
                {
                    _ui.BeginInvoke(() => HandlePaste(fg, cls));
                    return 1; // swallow
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    void HandlePaste(IntPtr window, string cls)
    {
        string? dir = null;
        try
        {
            dir = cls is "Progman" or "WorkerW"
                ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                : ExplorerFolder(window);
        }
        catch { }

        if (dir != null && Directory.Exists(dir))
            // "E:\" would end in \" and break argument parsing, so drive roots go as "E:\."
            Process.Start(new ProcessStartInfo(ExePath, $"paste \"{(dir.EndsWith('\\') ? dir + "." : dir)}\"") { UseShellExecute = false });
        else
            ReplayCtrlV();
    }

    static bool FileViewHasFocus(IntPtr fg)
    {
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(fg, out _), ref info)) return false;
        // DirectUIHWND = Explorer item view, SysListView32 = desktop icons.
        // Rename boxes, the address bar and search are other classes, so normal text paste still works.
        return ClassOf(info.hwndFocus) is "DirectUIHWND" or "SysListView32";
    }

    /// <summary>Path of the folder shown in the active tab of the given Explorer window.</summary>
    static string? ExplorerFolder(IntPtr hwnd)
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return null;
        dynamic shell = Activator.CreateInstance(type)!;
        string? fallback = null;
        foreach (dynamic w in shell.Windows())
        {
            try
            {
                if (Convert.ToInt64(w.HWND) != hwnd.ToInt64()) continue;
                string path = w.Document.Folder.Self.Path;
                fallback ??= path;
                // Windows 11 tabs share one top-level HWND; the active tab's view window is the visible one.
                if (w is IServiceProviderCom sp)
                {
                    var sid = SID_STopLevelBrowser;
                    var iid = typeof(IShellBrowser).GUID;
                    if (sp.QueryService(ref sid, ref iid, out var obj) == 0 && obj is IShellBrowser sb &&
                        sb.GetWindow(out var tab) == 0 && IsWindowVisible(tab))
                        return path;
                }
            }
            catch { }
        }
        return fallback;
    }

    static void ReplayCtrlV()
    {
        INPUT Key(ushort vk, bool up) => new()
        {
            type = 1,
            ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u, dwExtraInfo = Marker },
        };
        var inputs = new[] { Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        return h == IntPtr.Zero ? "" : GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    // ---------------------------------------------------------------- interop

    static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProviderCom
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd); // IOleWindow::GetWindow, first vtable slot
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public int vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] MOUSEINPUT _mi; // sizes the union correctly
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
}
