using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Resesh.Terminal;

namespace Resesh.App.Controls;

/// <summary>
/// Terminal font family field: an editable drop-down of the installed monospaced fonts, each
/// shown in its own typeface. The value is free text, because the setting is a fallback list
/// and may name a font that isn't installed here.
/// </summary>
public sealed partial class FontFamilyPicker : UserControl
{
    public static readonly DependencyProperty FamilyNameProperty = DependencyProperty.Register(
        nameof(FamilyName), typeof(string), typeof(FontFamilyPicker),
        new PropertyMetadata("", (d, _) => ((FontFamilyPicker)d).ShowFamilyName()));

    /// <summary>Draws the TextBlock it is set on in the named font (the drop-down's entries).</summary>
    public static readonly DependencyProperty PreviewFamilyProperty = DependencyProperty.RegisterAttached(
        "PreviewFamily", typeof(string), typeof(FontFamilyPicker),
        new PropertyMetadata(null, (d, e) =>
        {
            // The "Use app setting" entry is not a font; it keeps the UI font.
            if (d is TextBlock text && e.NewValue is string { Length: > 0 } name && name != AppSettingChoice)
                text.FontFamily = new FontFamily(name);
        }));

    public static string GetPreviewFamily(DependencyObject element) => (string)element.GetValue(PreviewFamilyProperty);

    public static void SetPreviewFamily(DependencyObject element, string value) => element.SetValue(PreviewFamilyProperty, value);

    /// <summary>The first entry when <see cref="ShowAppSettingChoice"/> is on; picking it clears the value.</summary>
    public const string AppSettingChoice = "Use app setting";

    private bool _ready;
    private bool _syncing;
    private IReadOnlyList<string> _fonts = [];

    public FontFamilyPicker()
    {
        InitializeComponent();
        Box.Loaded += OnBoxLoaded;
        Box.SelectionChanged += (_, _) =>
        {
            if (Box.SelectedItem is string name)
                Commit(name);
        };
        Box.TextSubmitted += (_, args) => Commit(args.Text);
        Box.LostFocus += (_, _) => Commit(Box.Text);
        // Rescan as the field is reached, so a font installed while the app runs shows up
        // without a restart. Focus usually comes first and gives the scan a head start.
        Box.GotFocus += (_, _) => _ = RefreshFontsAsync();
        Box.DropDownOpened += (_, _) => _ = RefreshFontsAsync();
    }

    /// <summary>The font family list, as typed or picked. Blank means "not set".</summary>
    public string FamilyName
    {
        get => (string)GetValue(FamilyNameProperty);
        set => SetValue(FamilyNameProperty, value);
    }

    public object? Header
    {
        get => Box.Header;
        set => Box.Header = value;
    }

    public string PlaceholderText
    {
        get => Box.PlaceholderText;
        set => Box.PlaceholderText = value;
    }

    /// <summary>Offers "Use app setting" above the fonts, for per-session overrides where a blank
    /// value means inheriting the app's font. Set before the control loads.</summary>
    public bool ShowAppSettingChoice { get; set; }

    /// <summary>The control that takes focus, for callers that move focus to this field.</summary>
    public Control FocusTarget => Box;

    private async void OnBoxLoaded(object sender, RoutedEventArgs e)
    {
        Box.Loaded -= OnBoxLoaded;
        // Automation names set on the picker belong to the field people actually reach.
        // Moved, not copied, so UI automation finds one element by the ID.
        AutomationProperties.SetAutomationId(Box, AutomationProperties.GetAutomationId(this));
        AutomationProperties.SetName(Box, AutomationProperties.GetName(this) is { Length: > 0 } name
            ? name
            : Header as string ?? "Terminal font");
        ClearValue(AutomationProperties.AutomationIdProperty);
        ClearValue(AutomationProperties.NameProperty);

        ShowFonts(await InstalledFonts.MonospaceFamiliesAsync());
        // An editable ComboBox clears Text set before it loads, and again when its items
        // change; apply the value only now.
        _ready = true;
        ShowFamilyName();
    }

    private async Task RefreshFontsAsync()
    {
        if (!_ready)
            return;
        var fonts = await InstalledFonts.MonospaceFamiliesAsync(refresh: true);
        if (_fonts.SequenceEqual(fonts))
            return;
        ShowFonts(fonts);
        ShowFamilyName(); // new items clear an editable ComboBox's text
    }

    private void ShowFonts(IReadOnlyList<string> fonts)
    {
        _fonts = fonts;
        // A concrete List lets CsWinRT generate the ItemsSource marshalling for AOT.
        List<string> items = ShowAppSettingChoice ? [AppSettingChoice, .. fonts] : [.. fonts];
        Box.ItemsSource = items;
    }

    private void ShowFamilyName()
    {
        if (!_ready || _syncing)
            return;
        _syncing = true;
        try
        {
            var value = FamilyName ?? "";
            if (value.Length == 0 && ShowAppSettingChoice)
                value = AppSettingChoice;
            Box.SelectedItem = (Box.ItemsSource as IReadOnlyList<string>)?
                .FirstOrDefault(name => name.Equals(value, StringComparison.OrdinalIgnoreCase));
            Box.Text = value;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Commit(string? text)
    {
        if (!_ready || _syncing)
            return;
        var value = (text ?? "").Trim();
        if (value.Equals(AppSettingChoice, StringComparison.OrdinalIgnoreCase))
            value = "";
        if (value == FamilyName)
            return;
        _syncing = true;
        try
        {
            FamilyName = value;
        }
        finally
        {
            _syncing = false;
        }
    }
}
