using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DevWorkspace;

internal static class DocumentPreview
{
    private static readonly FontFamily Mono = new("Consolas");

    public static FlowDocument Create(string text, bool markdown)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 14,
            PagePadding = new Thickness(16, 12, 16, 22),
            LineHeight = 22
        };
        document.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        if (!markdown)
        {
            document.Blocks.Add(new Paragraph(new Run(text)) { FontFamily = Mono, FontSize = 12 });
            return document;
        }

        foreach (var block in Markdown.Parse(text))
            document.Blocks.Add(RenderBlock(block, text));
        return document;
    }

    private static System.Windows.Documents.Block RenderBlock(Markdig.Syntax.Block block, string source)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = new Paragraph
                {
                    FontSize = heading.Level == 1 ? 25 : heading.Level == 2 ? 19 : 15,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 12, 0, 6)
                };
                RenderInlines(heading.Inline, title.Inlines);
                return title;
            case ParagraphBlock text:
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                RenderInlines(text.Inline, paragraph.Inlines);
                return paragraph;
            case CodeBlock code:
                var snippet = new Paragraph(new Run(code.Lines.ToString()))
                {
                    FontFamily = Mono, FontSize = 12,
                    Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 3, 0, 10)
                };
                snippet.SetResourceReference(TextElement.BackgroundProperty, "SurfaceRaised");
                return snippet;
            case ListBlock list:
                var renderedList = new System.Windows.Documents.List
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(22, 0, 0, 0)
                };
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var renderedItem = new ListItem();
                    foreach (var child in item)
                        renderedItem.Blocks.Add(RenderBlock(child, source));
                    renderedList.ListItems.Add(renderedItem);
                }
                return renderedList;
            case QuoteBlock quote:
                var section = new Section
                {
                    Padding = new Thickness(10, 6, 10, 2), Margin = new Thickness(0, 0, 0, 8)
                };
                section.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                section.SetResourceReference(TextElement.BackgroundProperty, "SurfaceRaised");
                foreach (var child in quote)
                    section.Blocks.Add(RenderBlock(child, source));
                return section;
            case ThematicBreakBlock:
                var rule = new Paragraph
                {
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Margin = new Thickness(0, 5, 0, 5), FontSize = 1, LineHeight = 1
                };
                rule.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, "Line");
                return rule;
            default:
                int start = Math.Clamp(block.Span.Start, 0, source.Length);
                int end = Math.Clamp(block.Span.End + 1, start, source.Length);
                return new Paragraph(new Run(source[start..end]));
        }
    }

    private static void RenderInlines(ContainerInline? container, InlineCollection output)
    {
        for (var inline = container?.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    output.Add(new Run(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    var snippet = new Run(code.Content) { FontFamily = Mono };
                    snippet.SetResourceReference(TextElement.BackgroundProperty, "SurfaceRaised");
                    output.Add(snippet);
                    break;
                case LineBreakInline { IsHard: true }:
                    output.Add(new LineBreak());
                    break;
                case LineBreakInline:
                    output.Add(new Run(" "));
                    break;
                case EmphasisInline emphasis:
                    Span styled = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    RenderInlines(emphasis, styled.Inlines);
                    output.Add(styled);
                    break;
                case ContainerInline child:
                    RenderInlines(child, output);
                    break;
                default:
                    output.Add(new Run(inline.ToString() ?? ""));
                    break;
            }
        }
    }
}
