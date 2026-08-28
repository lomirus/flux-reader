using System.ComponentModel;
using System.Runtime.InteropServices;
using FluxReader.Services;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace FluxReader.Interop;

internal sealed class SystemTrayIcon : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint TrayIconMessage = 0x8001;
    private const uint OpenCommandId = 1;
    private const uint RefreshCommandId = 2;
    private const uint ExitCommandId = 3;

    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x0010;
    private const uint LoadDefaultSize = 0x0040;
    private const uint NotifyIconMessageFlag = 0x0001;
    private const uint NotifyIconIconFlag = 0x0002;
    private const uint NotifyIconTipFlag = 0x0004;
    private const uint NotifyIconAdd = 0x0000;
    private const uint NotifyIconDelete = 0x0002;
    private const uint LeftButtonUp = 0x0202;
    private const uint RightButtonUp = 0x0205;
    private const uint MenuString = 0x0000;
    private const uint MenuSeparator = 0x0800;
    private const uint TrackRightButton = 0x0002;
    private const uint TrackReturnCommand = 0x0100;
    private const uint NullMessage = 0x0000;
    private const nuint SubclassId = 1;

    private readonly LocalizationService _localization;
    private readonly nint _windowHandle;
    private readonly nint _iconHandle;
    private readonly uint _taskbarCreatedMessage;
    private readonly WindowSubclassProcedure _windowSubclassProcedure;
    private bool _iconAdded;
    private bool _subclassInstalled;
    private bool _disposed;

    public SystemTrayIcon(Window window, LocalizationService localization, string iconPath)
    {
        _localization = localization;
        _windowHandle = WindowNative.GetWindowHandle(window);
        _windowSubclassProcedure = WindowSubclassCallback;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _iconHandle = LoadImage(
            nint.Zero,
            iconPath,
            ImageIcon,
            0,
            0,
            LoadFromFile | LoadDefaultSize);
        if (_iconHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _subclassInstalled = SetWindowSubclass(
            _windowHandle,
            _windowSubclassProcedure,
            SubclassId,
            0);
        if (!_subclassInstalled)
        {
            var error = Marshal.GetLastWin32Error();
            DestroyIcon(_iconHandle);
            throw new Win32Exception(error);
        }

        if (!AddIcon())
        {
            Dispose();
            throw new InvalidOperationException("Couldn't add the FluxReader system tray icon.");
        }
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? ExitRequested;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_iconAdded)
        {
            var iconData = CreateIconData();
            ShellNotifyIcon(NotifyIconDelete, ref iconData);
            _iconAdded = false;
        }

        if (_subclassInstalled)
        {
            RemoveWindowSubclass(_windowHandle, _windowSubclassProcedure, SubclassId);
            _subclassInstalled = false;
        }

        DestroyIcon(_iconHandle);
    }

    private bool AddIcon()
    {
        var iconData = CreateIconData();
        _iconAdded = ShellNotifyIcon(NotifyIconAdd, ref iconData);
        return _iconAdded;
    }

    private NotifyIconData CreateIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = TrayIconId,
        Flags = NotifyIconMessageFlag | NotifyIconIconFlag | NotifyIconTipFlag,
        CallbackMessage = TrayIconMessage,
        IconHandle = _iconHandle,
        Tip = "FluxReader",
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private nint WindowSubclassCallback(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == TrayIconMessage)
        {
            var notification = unchecked((uint)lParam.ToInt64());
            if (notification == LeftButtonUp)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (notification == RightButtonUp)
            {
                ShowContextMenu();
            }

            return nint.Zero;
        }

        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            _iconAdded = false;
            AddIcon();
            return nint.Zero;
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (!GetCursorPosition(out var cursorPosition))
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == nint.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MenuString, OpenCommandId, _localization.GetString("TrayOpen"));
            SetMenuDefaultItem(menu, OpenCommandId, false);
            AppendMenu(menu, MenuString, RefreshCommandId, _localization.GetString("RefreshAllFeeds"));
            AppendMenu(menu, MenuSeparator, 0, null);
            AppendMenu(menu, MenuString, ExitCommandId, _localization.GetString("TrayExit"));
            SetForegroundWindow(_windowHandle);

            var command = TrackPopupMenu(
                menu,
                TrackRightButton | TrackReturnCommand,
                cursorPosition.X,
                cursorPosition.Y,
                0,
                _windowHandle,
                nint.Zero);
            PostMessage(_windowHandle, NullMessage, 0, nint.Zero);

            if (command == OpenCommandId)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (command == RefreshCommandId)
            {
                RefreshRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (command == ExitCommandId)
            {
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowSubclassProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LoadImage(
        nint instance,
        string name,
        uint type,
        int desiredWidth,
        int desiredHeight,
        uint loadFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("comctl32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        WindowSubclassProcedure subclassProcedure,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        WindowSubclassProcedure subclassProcedure,
        nuint subclassId);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint DefSubclassProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint itemId, string? text);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(nint menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint windowHandle,
        nint rectangle);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPosition(out Point point);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint windowHandle, uint message, nuint wParam, nint lParam);
}
