using System.Runtime.InteropServices;

namespace FluxReader.Interop;

internal static class NativeCursor
{
    private const int ArrowCursorResourceId = 32512;
    private static readonly nint ArrowCursor = LoadCursor(nint.Zero, ArrowCursorResourceId);

    public static void SetArrow()
    {
        if (ArrowCursor != nint.Zero)
        {
            SetCursor(ArrowCursor);
        }
    }

    [DllImport("user32.dll", EntryPoint = "LoadCursorW", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LoadCursor(nint instance, nint cursorName);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetCursor(nint cursor);
}
