namespace WinRT;

/// <summary>Stands in for CsWinRT's attribute: these tests compile view models without CsWinRT.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class GeneratedBindableCustomPropertyAttribute : Attribute;
