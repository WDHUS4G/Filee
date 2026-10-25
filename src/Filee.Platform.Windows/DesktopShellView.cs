// Explorer's desktop view, the one that shows the desktop's icons. Shell.Application.Windows() lists the folder
// windows but not the desktop, so the desktop's view is found through ShellWindows.FindWindowSW instead: Elevation uses
// it to start a program through Explorer, and the selection shortcut to read the icons selected on the desktop.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Filee.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class DesktopShellView
{
    /// <summary>
    /// Runs <paramref name="use"/> with the desktop view's IShellFolderViewDual, a dispatch object (its SelectedItems,
    /// its Application). Throws when Explorer's desktop isn't there (another shell, Explorer restarting).
    /// </summary>
    public static T Use<T>(Func<object, T> use)
    {
        var shellWindowsType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!;
        object? shellWindows = null, desktop = null, browser = null, view = null, background = null;
        try
        {
            shellWindows = Activator.CreateInstance(shellWindowsType)
                           ?? throw new InvalidOperationException("ShellWindows is not available.");
            object location = CSIDL_DESKTOP;
            object? empty = null; // VT_EMPTY
            desktop = ((IShellWindows)shellWindows).FindWindowSW(ref location, ref empty, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH)
                      ?? throw new InvalidOperationException("Explorer's desktop window was not found.");
            var topLevelBrowser = ShellInterop.SID_STopLevelBrowser;
            var shellBrowserId = typeof(ShellInterop.IShellBrowser).GUID;
            browser = ((ShellInterop.IServiceProvider)desktop).QueryService(ref topLevelBrowser, ref shellBrowserId);
            view = ((ShellInterop.IShellBrowser)browser).QueryActiveShellView();
            var dispatchId = IID_IDispatch;
            background = ((IShellView)view).GetItemObject(SVGIO_BACKGROUND, ref dispatchId);
            return use(background);
        }
        finally
        {
            // Released here, on the caller's thread. Two of them may be one object.
            foreach (var item in new[] { background, view, browser, desktop, shellWindows }
                         .OfType<object>().Where(Marshal.IsComObject).Distinct(ReferenceEqualityComparer.Instance))
                Marshal.FinalReleaseComObject(item);
        }
    }

    private const int CSIDL_DESKTOP = 0;
    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;
    private const uint SVGIO_BACKGROUND = 0;
    private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");

    // Only the methods Filee calls are typed; the slots before them keep the vtable order and are never called.

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        void get_Count();
        void Item();
        void _NewEnum();
        void Register();
        void RegisterPending();
        void Revoke();
        void OnNavigate();
        void OnActivated();

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW(ref object location, ref object? locationRoot, int windowClass, out int hwnd, int options);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void TranslateAccelerator();
        void EnableModeless();
        void UIActivate();
        void Refresh();
        void CreateViewWindow();
        void DestroyViewWindow();
        void GetCurrentInfo();
        void AddPropertySheetPages();
        void SaveViewState();
        void SelectItem();

        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetItemObject(uint item, ref Guid riid);
    }
}
