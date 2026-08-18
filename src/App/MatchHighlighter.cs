using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Sessions.App;

/// <summary>Builds TextBlock inlines with case-insensitive filter matches emphasized.</summary>
public static class MatchHighlighter
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(MatchHighlighter), new PropertyMetadata("", OnChanged));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(MatchHighlighter), new PropertyMetadata("", OnChanged));

    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);
    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    public static void SetQuery(DependencyObject target, string value) => target.SetValue(QueryProperty, value);
    public static string GetQuery(DependencyObject target) => (string)target.GetValue(QueryProperty);

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock block)
            return;

        var text = GetText(block) ?? "";
        var terms = (GetQuery(block) ?? "").Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        block.Inlines.Clear();
        if (terms.Length == 0)
        {
            block.Inlines.Add(new Run { Text = text });
            return;
        }

        var position = 0;
        while (position < text.Length)
        {
            var nextIndex = -1;
            var nextLength = 0;
            foreach (var term in terms)
            {
                var index = text.IndexOf(term, position, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (nextIndex < 0 || index < nextIndex))
                {
                    nextIndex = index;
                    nextLength = term.Length;
                }
            }

            if (nextIndex < 0)
            {
                block.Inlines.Add(new Run { Text = text[position..] });
                break;
            }
            if (nextIndex > position)
                block.Inlines.Add(new Run { Text = text[position..nextIndex] });
            block.Inlines.Add(new Run
            {
                Text = text.Substring(nextIndex, nextLength),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["SessionSplitterHoverBrush"],
            });
            position = nextIndex + nextLength;
        }
    }
}
