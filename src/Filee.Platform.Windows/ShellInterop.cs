// COM interfaces of Explorer's shell browser, shared by the de-elevated restart (Elevation) and the selection lookup
// (ExplorerTabs). Only the methods Filee calls are typed; the slots before them keep the vtable order and are never
// called.

using System.Runtime.InteropServices;

namespace Filee.Platform.Windows;

internal static class ShellInterop
{
    /// <summary>Service id of the browser that hosts a shell view: the Explorer window, or with tabs the tab.</summary>
    public static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellBrowser
    {
        /// <summary>IOleWindow::GetWindow: with Explorer tabs, the tab's own ShellTabWindowClass window.</summary>
        [PreserveSig]
        int GetWindow(out nint hwnd);

        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();

        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryActiveShellView();
    }
}
