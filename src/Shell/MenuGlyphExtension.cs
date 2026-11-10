using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace DevWorkspace;

public sealed class MenuGlyphExtension(string glyph) : MarkupExtension
{
    public const string GitHistory = "\uE81C";
    public const string GitOperations = "\uE8F1";
    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");

    public override object ProvideValue(IServiceProvider serviceProvider) => Create(glyph);

    internal static TextBlock Create(string glyph) => new()
    {
        Text = glyph,
        FontFamily = IconFont,
        FontSize = 14,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
}
