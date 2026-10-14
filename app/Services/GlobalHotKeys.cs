using System.Runtime.InteropServices;
namespace LumaMusic.Services;

/// 全局媒体热键：窗口失焦、甚至最小化到托盘时也能控制播放。
/// 消息窗口与托盘图标同样的 HWND_MESSAGE 模式，RegisterHotKey 需要消息循环才能收到 WM_HOTKEY。
public sealed class GlobalHotKeys : IDisposable
{
    const uint WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    public enum Action { PlayPause, Previous, Next, ShowNowPlaying }
    readonly record struct Binding(int Id, uint Modifiers, int Key, Action Action);

    static readonly Binding[] Bindings =
    [
        new(1, MOD_CONTROL | MOD_ALT, 0x20, Action.PlayPause),      // Space
        new(2, MOD_CONTROL | MOD_ALT, 0x25, Action.Previous),       // Left
        new(3, MOD_CONTROL | MOD_ALT, 0x27, Action.Next),           // Right
        new(4, MOD_CONTROL | MOD_ALT, (int)'L', Action.ShowNowPlaying),
    ];

    public static string Description => "Ctrl + Alt + 空格 / ← / → / L";
    public event Action<Action>? Triggered;
    public IReadOnlyList<string> Failed { get; } = [];

    readonly IntPtr window;
    readonly WndProc proc;
    readonly List<string> failed = [];

    public GlobalHotKeys()
    {
        proc = WndProcHandler;
        // 复用托盘图标那个未导出的窗口类注册路径：这里自己注册一份独立类名。
        var wc = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = proc, lpszClassName = "LumaHotKeyWnd", hInstance = GetModuleHandleW(null) };
        if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410) // 1410 = 已注册
            throw new InvalidOperationException("无法注册热键消息窗口");
        window = CreateWindowExW(0, "LumaHotKeyWnd", "LumaHotKey", 0, 0, 0, 0, 0, (IntPtr)(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new InvalidOperationException("无法创建热键消息窗口");

        foreach (var b in Bindings)
            // MOD_NOREPEAT 防连发；注册失败多半是别的软件占了同一组合，只记不弹窗。
            if (!RegisterHotKey(window, b.Id, b.Modifiers | MOD_NOREPEAT, b.Key))
            {
                failed.Add(b.Action.ToString());
                AppPaths.Log($"GlobalHotKeys: {b.Action} 注册失败（可能被其他程序占用）");
            }
        Failed = failed;
    }

    IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            var id = (int)wParam;
            foreach (var b in Bindings)
                if (b.Id == id) { Triggered?.Invoke(b.Action); break; }
            return IntPtr.Zero;
        }
        if (msg == 0x0313) return IntPtr.Zero; // WM_HOTKEY 的释放消息，无需处理
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        foreach (var b in Bindings) UnregisterHotKey(window, b.Id);
        if (window != IntPtr.Zero) DestroyWindow(window);
    }

    delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowExW(uint exstyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, int vk);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
}