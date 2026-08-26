using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace FluxReader.Controls;

public sealed class HorizontalResizeSplitter : Grid
{
    public HorizontalResizeSplitter()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
