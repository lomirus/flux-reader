using System.ComponentModel;
using System.Runtime.InteropServices;
using FluxReader.Services;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace FluxReader.Interop;

// A pinned taskbar button can use its shortcut's icon instead of the window
// icon. Use the shell's status overlay rather than replacing that base icon.
internal sealed class TaskbarUnreadIndicator : IDisposable
{
    private readonly nint _windowHandle;
    private readonly nint _overlayIconHandle;
    private readonly ITaskbarList3 _taskbarList;
    private readonly LocalizationService _localization;
    private bool _hasUnreadArticles;
    private bool _taskbarButtonCreated;
    private bool _disposed;

    public TaskbarUnreadIndicator(Window window, LocalizationService localization)
    {
        _windowHandle = WindowNative.GetWindowHandle(window);
        _localization = localization;
        _overlayIconHandle = LoadImage(
            nint.Zero,
            Path.Combine(AppContext.BaseDirectory, "Assets", "fluxreader-unread-overlay.ico"),
            1, 16, 16, 0x0010);
        if (_overlayIconHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            _taskbarList = (ITaskbarList3)new TaskbarList();
            _taskbarList.HrInit();
        }
        catch
        {
            if (_taskbarList is not null)
            {
                Marshal.FinalReleaseComObject(_taskbarList);
            }

            DestroyIcon(_overlayIconHandle);
            throw;
        }
    }

    public void SetHasUnreadArticles(bool hasUnreadArticles)
    {
        if (_disposed || _hasUnreadArticles == hasUnreadArticles)
        {
            return;
        }

        _hasUnreadArticles = hasUnreadArticles;
        ApplyOverlay();
    }

    public void OnTaskbarButtonCreated()
    {
        if (_disposed)
        {
            return;
        }

        // Wait for Explorer to create the button before setting its overlay.
        // Reapply even when unread state is unchanged after Explorer restarts.
        _taskbarButtonCreated = true;
        ApplyOverlay();
    }

    private void ApplyOverlay()
    {
        if (!_taskbarButtonCreated)
        {
            return;
        }

        var result = _taskbarList.SetOverlayIcon(
            _windowHandle,
            _hasUnreadArticles ? _overlayIconHandle : nint.Zero,
            _hasUnreadArticles ? _localization.GetString("UnreadArticles") : string.Empty);
        if (result < 0)
        {
            DiagnosticLog.Warning("taskbar.overlay_update_failed", new { hasUnreadArticles = _hasUnreadArticles, hresult = result });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_taskbarButtonCreated)
        {
            _taskbarList.SetOverlayIcon(_windowHandle, nint.Zero, string.Empty);
        }

        Marshal.FinalReleaseComObject(_taskbarList);
        DestroyIcon(_overlayIconHandle);
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    private class TaskbarList;

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // Inherited methods must precede ITaskbarList3 methods in COM vtable order.
        void HrInit();
        void AddTab(nint windowHandle);
        void DeleteTab(nint windowHandle);
        void ActivateTab(nint windowHandle);
        void SetActiveAlt(nint windowHandle);
        void MarkFullscreenWindow(nint windowHandle, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(nint windowHandle, ulong completed, ulong total);
        void SetProgressState(nint windowHandle, uint state);
        void RegisterTab(nint tabHandle, nint windowHandle);
        void UnregisterTab(nint tabHandle);
        void SetTabOrder(nint tabHandle, nint insertBeforeHandle);
        void SetTabActive(nint tabHandle, nint windowHandle, uint reserved);
        void ThumbBarAddButtons(nint windowHandle, uint count, nint buttons);
        void ThumbBarUpdateButtons(nint windowHandle, uint count, nint buttons);
        void ThumbBarSetImageList(nint windowHandle, nint imageList);

        [PreserveSig]
        int SetOverlayIcon(nint windowHandle, nint iconHandle, [MarshalAs(UnmanagedType.LPWStr)] string description);

        void SetThumbnailTooltip(nint windowHandle, [MarshalAs(UnmanagedType.LPWStr)] string tooltip);
        void SetThumbnailClip(nint windowHandle, nint clip);
    }

    [DllImport("user32.dll", EntryPoint = "LoadImageW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);
}
