using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluxReader.Controls;

public sealed class FeedTreeViewItem : TreeViewItem
{
    private const double ChevronGlyphSize = 14;
    private const double ChevronBoxSize = 16;
    private const double ChevronHorizontalPadding = 8;

    public FeedTreeViewItem()
    {
        GlyphSize = ChevronGlyphSize;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("ExpandCollapseChevron") is Grid chevron)
        {
            chevron.Padding = new Thickness(ChevronHorizontalPadding, 0, ChevronHorizontalPadding, 0);
        }

        ConfigureGlyph(GetTemplateChild("CollapsedGlyph") as TextBlock);
        ConfigureGlyph(GetTemplateChild("ExpandedGlyph") as TextBlock);
    }

    private static void ConfigureGlyph(TextBlock? glyph)
    {
        if (glyph is null)
        {
            return;
        }

        glyph.Width = ChevronBoxSize;
        glyph.Height = ChevronBoxSize;
        glyph.Padding = new Thickness(1);
    }
}
