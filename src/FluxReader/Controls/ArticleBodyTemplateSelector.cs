using FluxReader.Core.Models;
using FluxReader.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluxReader.Controls;

public sealed class ArticleBodyTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }

    public DataTemplate? ImageTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        item is ArticleBodyBlock { Kind: ArticleContentBlockKind.Image }
            ? ImageTemplate
            : TextTemplate;
}
