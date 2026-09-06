using System.Runtime.InteropServices;
namespace LumaMusic.Services;
// 系统托盘图标：Shell_NotifyIcon + 隐藏消息窗口接收回调，右键弹原生菜单（打开/退出）。
public sealed class TrayIcon : IDisposable
{
    const uint WM_APP_TRAY=0x8100;
    const uint NIM_ADD=0,NIM_DELETE=2,NIF_MESSAGE=1,NIF_ICON=2,NIF_TIP=4;
    const int WM_LBUTTONUP=0x0202,WM_RBUTTONUP=0x0205;
    public event Action? OpenRequested;
    public event Action? ExitRequested;
    readonly IntPtr window;readonly IntPtr icon;readonly uint id=(uint)Random.Shared.Next(0x100,0xFFFF);
    bool added;
    readonly WndProc proc; // 防 GC 回收委托
    public TrayIcon(string iconPath)
    {
        proc=WndProcHandler;
        new ClassRegistrar("LumaTrayWnd",proc);
        window=CreateWindowExW(0,"LumaTrayWnd","LumaTray",0,0,0,0,0,(IntPtr)(-3),IntPtr.Zero,IntPtr.Zero,IntPtr.Zero); // HWND_MESSAGE
        icon=LoadImageW(GetModuleHandleW(null),iconPath,1,0,0,0x0010); // IMAGE_ICON + LR_DEFAULTSIZE
    }
    public void Add()
    {
        if(added||window==IntPtr.Zero)return;
        var data=Data(NIM_ADD);added=Shell_NotifyIconW(NIM_ADD,ref data);
    }
    public void Dispose()
    {
        if(added){var data=Data(NIM_DELETE);Shell_NotifyIconW(NIM_DELETE,ref data);added=false;}
        if(window!=IntPtr.Zero)DestroyWindow(window);
        if(icon!=IntPtr.Zero)DestroyIcon(icon);
    }
    NOTIFYICONDATA Data(uint message)=>new(){cbSize=(uint)Marshal.SizeOf<NOTIFYICONDATA>(),hWnd=window,uID=id,uFlags=NIF_MESSAGE|NIF_ICON|NIF_TIP,uCallbackMessage=WM_APP_TRAY,hIcon=icon,tip="Luma Music"};
    IntPtr WndProcHandler(IntPtr hWnd,uint msg,IntPtr wParam,IntPtr lParam)
    {
        if(msg==WM_APP_TRAY){
            var low=lParam.ToInt64()&0xFFFF;
            if(low==WM_LBUTTONUP)OpenRequested?.Invoke();
            else if(low==WM_RBUTTONUP)ShowMenu();
        }
        else if(msg==0x0010)Dispose(); // WM_CLOSE
        return DefWindowProcW(hWnd,msg,wParam,lParam);
    }
    void ShowMenu()
    {
        var menu=CreatePopupMenu();
        AppendMenuW(menu,0,(UIntPtr)1,"打开 Luma Music");
        AppendMenuW(menu,0,(UIntPtr)2,"退出");
        GetCursorPos(out var p);
        SetForegroundWindow(window); // 使菜单在失焦时自动关闭
        var cmd=TrackPopupMenuEx(menu,0x0180,p.X,p.Y,window,IntPtr.Zero); // TPM_RETURNCMD|TPM_NONOTIFY
        DestroyMenu(menu);
        if(cmd==1)OpenRequested?.Invoke();
        else if(cmd==2)ExitRequested?.Invoke();
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID,uFlags,uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string tip;
    }
    delegate IntPtr WndProc(IntPtr hWnd,uint msg,IntPtr wParam,IntPtr lParam);
    sealed class ClassRegistrar : IDisposable
    {
        public ClassRegistrar(string name,WndProc proc)
        {
            var wc=new WNDCLASSEXW{cbSize=(uint)Marshal.SizeOf<WNDCLASSEXW>(),lpfnWndProc=proc,lpszClassName=name,hInstance=GetModuleHandleW(null)};
            RegisterClassExW(ref wc);
        }
        public void Dispose(){}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct WNDCLASSEXW
    {
        public uint cbSize,style;
        public WndProc lpfnWndProc;
        public int cbClsExtra,cbWndExtra;
        public IntPtr hInstance,hIcon,hCursor,hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]public string lpszClassName;
        public IntPtr hIconSm;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateWindowExW(uint exstyle,string cls,string title,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr inst,IntPtr param);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr DefWindowProcW(IntPtr hWnd,uint msg,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",SetLastError=true)]static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool Shell_NotifyIconW(uint message,ref NOTIFYICONDATA data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr LoadImageW(IntPtr hInst,string name,uint type,int cx,int cy,uint load);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll")]static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")]static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool AppendMenuW(IntPtr menu,uint flags,UIntPtr id,string text);
    [DllImport("user32.dll")]static extern int TrackPopupMenuEx(IntPtr menu,uint flags,int x,int y,IntPtr hWnd,IntPtr rect);
    [DllImport("user32.dll")]static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")]static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]static extern bool SetForegroundWindow(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)]struct POINT{public int X,Y;}
}
