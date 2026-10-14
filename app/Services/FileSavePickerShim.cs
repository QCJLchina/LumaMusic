using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Storage;

namespace LumaMusic;

/// 原生 Windows.Storage.Pickers.FileSavePicker 的 COM 转发。
/// 当前 Windows App SDK 没有为它提供 C# 投影（FolderPicker/FileOpenPicker 有，SavePicker 没有），
/// 所以这里声明 IFileSavePicker 自己转发放激活出来的实例。方法签名顺序与 WinRT 元数据一致。
[ComImport]
[Guid("84BCCA7E-3AE1-4D4D-B8C9-E8D6BEA1A5B8")]
[InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
interface IFileSavePickerRaw
{
    void SetFileTypeChoices([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] string[] valueTypes, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] string[] choices);
    void SetSuggestedFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
    void PickSaveFileAsync(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object result);
}

static class FileSavePickerShim
{

    public static async Task<StorageFile?> PickAsync(IntPtr hwnd, string suggestedName, string displayName, string extension)
    {
        var type = Type.GetType("Windows.Storage.Pickers.FileSavePicker, Windows");
        if (type is null) throw new InvalidOperationException("系统未提供保存对话框");
        var picker = Activator.CreateInstance(type)!;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        // RoGetActivationFactory 得到的实例自带投影，转成自己的接口转发调用。
        var unknown = Marshal.GetIUnknownForObject(picker);
        try
        {
            var iid = typeof(IFileSavePickerRaw).GUID;
            var hr = Marshal.QueryInterface(unknown, ref iid, out var ppv);
            if (hr < 0) throw new InvalidOperationException("无法初始化保存对话框（0x" + hr.ToString("X8") + "）");
            try
            {
                var raw = (IFileSavePickerRaw)Marshal.GetObjectForIUnknown(ppv);
                raw.SetFileTypeChoices([displayName], [extension]);
                raw.SetSuggestedFileName(suggestedName);
                var riid = typeof(IAsyncOperation<StorageFile>).GUID;
                raw.PickSaveFileAsync(ref riid, out var result);
                var typed=(IAsyncOperation<StorageFile?>)Marshal.GetTypedObjectForIUnknown(
                    Marshal.GetIUnknownForObject(result),typeof(IAsyncOperation<StorageFile?>));
                return await typed;
            }
            finally { Marshal.Release(ppv); }
        }
        finally { Marshal.Release(unknown); }
    }
}