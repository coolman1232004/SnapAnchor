using SnapAnchor.Controls;
using SnapAnchor.Services;
using SnapAnchor.Windows;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SnapAnchor.RecognitionSmoke;

internal static class WorkspaceUiSmoke
{
    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"snapanchor-workspace-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("SNAPANCHOR_HISTORY_ROOT");
        Environment.SetEnvironmentVariable("SNAPANCHOR_HISTORY_ROOT", root);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SnapAnchor;component/Resources/ToolbarResources.xaml", UriKind.Relative) });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SnapAnchor;component/Resources/ApplicationTheme.xaml", UriKind.Relative) });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        MainWindow? window = null;
        try
        {
            LocalizationService.Configure(LocalizationService.English);
            ApplicationThemeService.Apply("Light");
            for (var index = 0; index < 9; index++)
            {
                var record = HistoryService.Add(Sample(index), sourceKind: "Capture");
                HistoryService.UpdateMetadata(record.Id, title: $"Design reference {index + 1}", tags: [index == 0 ? "needle" : "design"], favorite: index == 0);
            }
            window = new MainWindow(layoutPreview: true);
            var host = (ContentControl)window.FindName("PageHost");
            var history = host.Content as HistoryView ?? throw new InvalidOperationException("Workspace must show the real history view.");
            Pump(history.ReloadAsync(true));
            var list = (ListBox)history.FindName("HistoryList");
            Require(list.Items.Count == 6, "Workspace shows six recent captures.");
            Require(((FrameworkElement)history.FindName("PaginationPanel")).Visibility == Visibility.Collapsed, "Workspace hides pagination.");
            Layout(window, new Size(1100, 730));
            Require(((StackPanel)history.FindName("SelectedActions")).IsEnabled, "A selected capture enables shared actions.");
            Require(((TextBlock)history.FindName("SearchHint")).Visibility == Visibility.Visible, "An empty search field shows its purpose.");
            Render(window, "workspace-light", new Size(1100, 730));

            ((RadioButton)window.FindName("HistoryNavigation")).IsChecked = true;
            Pump(history.ReloadAsync(true));
            Require(ReferenceEquals(host.Content, history) && list.Items.Count == 9, "History reuses the view and shows the full library.");
            ((TextBox)history.FindName("SearchBox")).Text = "needle";
            Pump(history.ReloadAsync(true));
            Layout(window, new Size(1100, 730));
            Require(((TextBlock)history.FindName("SearchHint")).Visibility == Visibility.Collapsed, "The search hint clears while entering a query.");
            Require(list.Items.Count == 1, "History search includes tags.");
            ((TextBox)history.FindName("SearchBox")).Clear();
            ((CheckBox)history.FindName("FavoriteOnlyBox")).IsChecked = true;
            Pump(history.ReloadAsync(true));
            Require(list.Items.Count == 1, "Favourite filtering is preserved.");
            ((CheckBox)history.FindName("FavoriteOnlyBox")).IsChecked = false;
            var selectedId = (string)list.Items[0].GetType().GetProperty("Id")!.GetValue(list.Items[0])!;
            HistoryService.Delete(selectedId);
            ((CheckBox)history.FindName("ShowRecycleBox")).IsChecked = true;
            Pump(history.ReloadAsync(true));
            Require(list.Items.Count == 1, "Recycle bin remains accessible.");
            HistoryService.Restore(selectedId);
            ((RadioButton)window.FindName("WorkspaceNavigation")).IsChecked = true;
            Pump(history.ReloadAsync(true));
            Require(list.Items.Count == 6 && ((CheckBox)history.FindName("ShowRecycleBox")).IsChecked == false, "Returning home restores the recent-capture view.");
            Layout(window, new Size(900, 650));
            var scrollViewer = VisualDescendants(list).OfType<ScrollViewer>().First();
            scrollViewer.ScrollToVerticalOffset(80);
            Layout(window, new Size(900, 650));
            Require(scrollViewer.VerticalOffset > 0 && VisualDescendants(list).OfType<System.Windows.Controls.Primitives.ScrollBar>().Any(bar => bar.Orientation == Orientation.Vertical && bar.Maximum > 0), "The themed gallery remains scrollable.");
            scrollViewer.ScrollToTop();

            ((RadioButton)window.FindName("SettingsNavigation")).IsChecked = true;
            var preferences = host.Content as PreferencesView ?? throw new InvalidOperationException("Settings must open inside the workspace.");
            var tabs = (TabControl)preferences.FindName("Tabs");
            Require(tabs.Items.Count == 10 && tabs.TabStripPlacement == Dock.Left, "All existing settings categories remain accessible in a sidebar.");
            Require(!preferences.HasUnsavedChanges, "Opening settings does not create unsaved changes.");
            foreach (var size in new[] { new Size(1100, 730), new Size(900, 650), new Size(700, 520) })
            {
                for (var tab = 0; tab < tabs.Items.Count; tab++)
                {
                    tabs.SelectedIndex = tab;
                    Layout(window, size);
                    Layout(window, size);
                    var scroll = (ScrollViewer)((TabItem)tabs.SelectedItem).Content;
                    Require(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled, "Settings must reflow instead of using horizontal scrolling.");
                    if (tab == 1)
                    {
                        var field = (ColorField)preferences.FindName("BorderColorBox");
                        var bounds = field.TransformToAncestor(scroll).TransformBounds(new Rect(field.RenderSize));
                        Require(bounds.Right <= scroll.ActualWidth + 1, $"Color picker fits at {size.Width}px.");
                    }
                }
            }
            Require(!preferences.HasUnsavedChanges, "Browsing settings categories does not create unsaved changes.");
            tabs.SelectedIndex = 0;
            Render(window, "settings-light", new Size(1100, 730));
            tabs.SelectedIndex = 2;
            Render(window, "settings-toolbar", new Size(900, 650));
            var appearance = (ComboBox)preferences.FindName("AppearanceBox");
            Require(appearance.FontWeight == FontWeights.Normal, "Selecting a settings category does not make its whole form bold.");
            appearance.SelectedValue = "Dark";
            Require(preferences.HasUnsavedChanges, "Editing settings marks the draft as changed.");
            Require(SettingsService.Load().AppearanceMode == "System", "Draft changes are not saved early.");
            var save = Descendants(preferences).OfType<Button>().Single(button => button.Content as string == "Save changes");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(SettingsService.Load().AppearanceMode == "Dark", "Explicit save persists the theme.");
            Require(host.Content is HistoryView && ((RadioButton)window.FindName("WorkspaceNavigation")).IsChecked == true, "Saving settings returns to the workspace.");
            Require(window.Background is SolidColorBrush brush && brush.Color == Color.FromRgb(32, 33, 36), "Theme changes update the existing window.");
            Require(list.Foreground is SolidColorBrush ink && ink.Color == Color.FromRgb(244, 244, 245), "Gallery titles remain readable in dark mode.");
            var overlay = new CaptureOverlayWindow(Sample(0));
            var editor = (AnnotationEditorControl)overlay.FindName("CaptureInlineEditor");
            Require(((SolidColorBrush)editor.FindResource("ToolbarSurfaceBrush")).Color == Color.FromRgb(43, 44, 48), "Capture and annotation toolbars share the dark theme.");
            ApplicationThemeService.Apply("Light");
            Require(((SolidColorBrush)editor.FindResource("ToolbarSurfaceBrush")).Color == Colors.White, "Existing capture toolbars follow theme changes.");
            ApplicationThemeService.Apply("Dark");
            overlay.Close();
            Pump(history.ReloadAsync(true));
            Render(window, "workspace-dark", new Size(1100, 730));
            Require(ColorField.TryParse("#660067C0", out var color) && color.A == 0x66 && !ColorField.TryParse("not-a-color", out _), "ARGB alpha and invalid-color handling work.");

            window.ShowPage("settings");
            preferences = (PreferencesView)host.Content;
            ((ComboBox)preferences.FindName("AppearanceBox")).SelectedValue = "Light";
            Descendants(preferences).OfType<Button>().Single(button => button.Content as string == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(host.Content is HistoryView && SettingsService.Load().AppearanceMode == "Dark", "Cancelling discards the draft without saving or applying its theme.");

            foreach (var language in new[] { LocalizationService.TraditionalChinese, LocalizationService.SimplifiedChinese, LocalizationService.English })
            {
                window.DisposeLayoutPreview();
                var localizedSettings = SettingsService.Load();
                localizedSettings.UiLanguage = language;
                SettingsService.Save(localizedSettings);
                LocalizationService.Configure(language);
                window = new MainWindow(layoutPreview: true);
                host = (ContentControl)window.FindName("PageHost");
                window.ShowPage("settings");
                preferences = (PreferencesView)host.Content;
                tabs = (TabControl)preferences.FindName("Tabs");
                for (var tab = 0; tab < tabs.Items.Count; tab++) { tabs.SelectedIndex = tab; Layout(window, new Size(900, 650)); }
                tabs.SelectedIndex = 0;
                Render(window, language == LocalizationService.TraditionalChinese ? "settings-zh-hant" : language == LocalizationService.SimplifiedChinese ? "settings-zh-hans" : "settings-dark", new Size(900, 650));
            }
            window.ShowPage("workspace");
            history = (HistoryView)host.Content;
            Pump(history.ReloadAsync(true));
            Render(window, "workspace-dpi150", new Size(900, 650), 1.5);
            Render(window, "workspace-dpi200", new Size(700, 520), 2);
            Console.WriteLine("WORKSPACE UI: navigation, recent/history data, search, favourites, recycle/restore, explicit settings save, live theme, ARGB colors, all ten categories, three languages and constrained/DPI layouts verified.");
            return 0;
        }
        finally
        {
            window?.DisposeLayoutPreview();
            application.Shutdown();
            Environment.SetEnvironmentVariable("SNAPANCHOR_HISTORY_ROOT", previous);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Layout(MainWindow window, Size size)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
    }
    private static void Render(MainWindow window, string name, Size size, double scale = 1)
    {
        var output = Environment.GetEnvironmentVariable("SNAPANCHOR_UI_RENDER_ROOT");
        if (string.IsNullOrWhiteSpace(output)) return;
        Layout(window, size);
        var image = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render((Visual)window.Content); image.Freeze();
        Directory.CreateDirectory(output);
        CaptureService.SavePng(image, Path.Combine(output, name + ".png"));
    }
    private static BitmapSource Sample(int index)
    {
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(225 + index * 2), 233, 243)), null, new Rect(0, 0, 640, 360));
            draw.DrawRoundedRectangle(Brushes.White, null, new Rect(60, 35, 520, 290), 6, 6);
            var ink = new SolidColorBrush(Color.FromRgb(95, 118, 150));
            draw.DrawRectangle(ink, null, new Rect(90, 70, 200, 12));
            for (var line = 0; line < 7; line++) draw.DrawRectangle(ink, null, new Rect(90, 110 + line * 23, 300 + line % 3 * 45, 5));
        }
        var image = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
