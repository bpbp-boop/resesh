using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Resesh.Core.Agents;

namespace Resesh.App.Controls;

/// <summary>
/// The agent state badge on a tab and the workspace rail: a status-coloured disc in the app's
/// own colours, cut out of whatever is under it by a ring, with the state's symbol on it.
/// Shaped like the Fluent InfoBadge, but drawn from ellipses and text at their real size:
/// the InfoBadge scales a 20 px icon glyph down through a Viewbox, which comes out jagged
/// and faint at tab size.
/// </summary>
public sealed partial class AgentBadge : Grid
{
    /// <summary>The ring's width on each side.</summary>
    private const double Ring = 1;

    private readonly Ellipse _ring = new();
    private readonly Ellipse _disc = new();
    private readonly SolidColorBrush _discBrush = new();
    private readonly TextBlock _symbol = new()
    {
        FontWeight = FontWeights.Bold,
        TextLineBounds = TextLineBounds.Tight,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsTextScaleFactorEnabled = false,
    };

    private AgentAttention _attention;
    private double _size = 6;

    public AgentBadge()
    {
        IsHitTestVisible = false;
        _disc.Fill = _discBrush;
        _disc.Margin = new Thickness(Ring);
        ThemeResources.SetBrush(_symbol, TextBlock.ForegroundProperty, "SessionBadgeGlyphBrush");
        Children.Add(_ring);
        Children.Add(_disc);
        Children.Add(_symbol);
        Update();
    }

    /// <summary>The state shown. Working and a bell show a plain dot; the others a symbol.</summary>
    public AgentAttention Attention
    {
        get => _attention;
        set { _attention = value; Update(); }
    }

    /// <summary>The disc's diameter, without the ring.</summary>
    public double Size
    {
        get => _size;
        set { _size = value; Update(); }
    }

    /// <summary>The colour behind the badge, which the ring repeats.</summary>
    public Brush? RingBrush
    {
        get => _ring.Fill;
        set => _ring.Fill = value;
    }

    private void Update()
    {
        Width = Height = _size + 2 * Ring;
        _discBrush.Color = PresentationValues.AgentBadgeColor(_attention);
        var symbol = Symbol(_attention);
        _symbol.Text = symbol;
        _symbol.Visibility = symbol.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Tight line bounds centre the ink, so the symbol sits in the middle of the disc.
        _symbol.FontSize = Math.Round(_size * 0.72);
        // The tick's ink is right-heavy (its long stroke), so centring its box puts it right of
        // the middle; a right margin moves it back by half that.
        _symbol.Margin = _attention == AgentAttention.Complete ? new Thickness(0, 0, _size * 0.06, 0) : default;
    }

    /// <summary>A symbol as well as a colour, so the badge reads without telling the colours apart.</summary>
    private static string Symbol(AgentAttention attention) => attention switch
    {
        AgentAttention.NeedsApproval => "!",
        AgentAttention.NeedsAnswer => "?",
        AgentAttention.Complete => "\u2713",
        AgentAttention.Failed => "\u2715",
        _ => "",
    };
}
