using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Collections;

namespace SnapAnchor.Services;

internal static class LocalizationService
{
    internal const string English = "English";
    internal const string SimplifiedChinese = "简体中文";
    internal const string TraditionalChinese = "繁體中文";

    private static bool _automaticLocalizationEnabled;

    private static readonly System.Resources.ResourceManager Strings =
        new("SnapAnchor.Resources.Strings", typeof(LocalizationService).Assembly);
    private static readonly System.Globalization.CultureInfo SimplifiedCulture =
        System.Globalization.CultureInfo.GetCultureInfo("zh-Hans");
    private static readonly System.Globalization.CultureInfo TraditionalCulture =
        System.Globalization.CultureInfo.GetCultureInfo("zh-Hant");

    internal static string CurrentLanguage { get; private set; } = English;

    internal static string Normalize(string? language) => language is SimplifiedChinese or TraditionalChinese ? language : English;

    internal static void Configure(string? language) => CurrentLanguage = Normalize(language);

    internal static string Current(string text) => Translate(text, CurrentLanguage);

    internal static string Format(string format, params object?[] args) => string.Format(Current(format), args);

    internal static string Translate(string text, string? language)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var normalized = Normalize(language);
        if (normalized == English) return text;
        var culture = normalized == SimplifiedChinese ? SimplifiedCulture : TraditionalCulture;
        return Strings.GetString(text, culture) is { Length: > 0 } translated ? translated : text;
    }

    internal static IReadOnlyList<string> MissingTranslations(string? language)
    {
        var normalized = Normalize(language);
        if (normalized == English) return [];
        var culture = normalized == SimplifiedChinese ? SimplifiedCulture : TraditionalCulture;
        var source = Strings.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, false);
        var translated = Strings.GetResourceSet(culture, true, false);
        if (source is null || translated is null) return ["<resource set>"];
        return source.Cast<DictionaryEntry>()
            .Select(entry => entry.Key as string)
            .Where(key => key is not null && string.IsNullOrWhiteSpace(translated.GetString(key)))
            .Cast<string>()
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
    }

    internal static void EnableAutomaticLocalization()
    {
        if (_automaticLocalizationEnabled) return;
        _automaticLocalizationEnabled = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply((DependencyObject)sender, CurrentLanguage)));
        EventManager.RegisterClassHandler(typeof(UserControl), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply((DependencyObject)sender, CurrentLanguage)));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) => Apply((DependencyObject)sender, CurrentLanguage)));
    }

    internal static void Apply(DependencyObject root, string? language)
    {
        var normalized = Normalize(language);
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        ApplyTree(root, normalized, visited);
    }

    private static void ApplyTree(DependencyObject value, string language, ISet<DependencyObject> visited)
    {
        if (!visited.Add(value)) return;
        ApplyElement(value, language);

        foreach (var child in LogicalTreeHelper.GetChildren(value).OfType<DependencyObject>())
            ApplyTree(child, language, visited);

        var count = value is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(value) : 0;
        for (var index = 0; index < count; index++)
            ApplyTree(VisualTreeHelper.GetChild(value, index), language, visited);
    }

    private static void ApplyElement(DependencyObject value, string language)
    {
        if (value is Window window) window.Title = Translate(window.Title, language);

        if (value is TextBlock text && !BindingOperations.IsDataBound(text, TextBlock.TextProperty))
        {
            var topLevelInlines = text.Inlines.ToList();
            if (topLevelInlines.All(inline => inline is Run))
            {
                if (!string.IsNullOrWhiteSpace(text.Text)) text.Text = Translate(text.Text, language);
            }
            else
            {
                foreach (var run in DescendantRuns(text.Inlines).ToList())
                    if (!string.IsNullOrWhiteSpace(run.Text)) run.Text = Translate(run.Text, language);
            }
        }

        if (value is HeaderedContentControl headered &&
            !BindingOperations.IsDataBound(headered, HeaderedContentControl.HeaderProperty) && headered.Header is string header)
            headered.Header = Translate(header, language);
        if (value is HeaderedItemsControl headeredItems &&
            !BindingOperations.IsDataBound(headeredItems, HeaderedItemsControl.HeaderProperty) && headeredItems.Header is string itemsHeader)
            headeredItems.Header = Translate(itemsHeader, language);
        if (value is ContentControl content &&
            !BindingOperations.IsDataBound(content, ContentControl.ContentProperty) && content.Content is string label)
            content.Content = Translate(label, language);
        if (value is FrameworkElement element &&
            !BindingOperations.IsDataBound(element, FrameworkElement.ToolTipProperty) && element.ToolTip is string tip)
            element.ToolTip = Translate(tip, language);

        if (!BindingOperations.IsDataBound(value, System.Windows.Automation.AutomationProperties.NameProperty) &&
            System.Windows.Automation.AutomationProperties.GetName(value) is { Length: > 0 } accessibleName)
            System.Windows.Automation.AutomationProperties.SetName(value, Translate(accessibleName, language));

        if (value is ListView { View: GridView grid })
            foreach (var column in grid.Columns)
                if (column.Header is string columnHeader) column.Header = Translate(columnHeader, language);
    }

    private static IEnumerable<Run> DescendantRuns(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run) yield return run;
            if (inline is Span span)
                foreach (var child in DescendantRuns(span.Inlines)) yield return child;
        }
    }

}
