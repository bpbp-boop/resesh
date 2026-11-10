using WinRT;

// WinRT structs and enums that the app or a library it ships unboxes from a WinRT value
// (DependencyObject.GetValue, a resource lookup). Under Native AOT, C#/WinRT can only unbox
// a type that something roots, so without an entry here the cast throws InvalidCastException
// and `is T` quietly answers false. eng/WinRTUnboxCheck fails the build when one is missing.

// Toolkit GridSplitter, on every drag.
[assembly: GeneratedWinRTExposedExternalType(typeof(Microsoft.UI.Xaml.GridLength))]
// Toolkit SizerBase.
[assembly: GeneratedWinRTExposedExternalType(typeof(Microsoft.UI.Xaml.Controls.Orientation))]
[assembly: GeneratedWinRTExposedExternalType(typeof(Microsoft.UI.Input.InputSystemCursorShape))]
// CommandsPanel's OverlayCornerRadius lookup; toolkit CornerRadiusConverter.
[assembly: GeneratedWinRTExposedExternalType(typeof(Microsoft.UI.Xaml.CornerRadius))]
// CompactTreeIndentationConverter; toolkit ScrollViewerExtensions.
[assembly: GeneratedWinRTExposedExternalType(typeof(Microsoft.UI.Xaml.Thickness))]
// ThemeVisualPalette; toolkit ColorHelper and AttachedShadowBase.
[assembly: GeneratedWinRTExposedExternalType(typeof(Windows.UI.Color))]
// Toolkit UserHandPreferenceStateTrigger.
[assembly: GeneratedWinRTExposedExternalType(typeof(Windows.UI.ViewManagement.HandPreference))]
