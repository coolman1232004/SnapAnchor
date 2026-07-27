using SnapAnchor.Services;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SnapAnchor.Windows;

internal sealed class UpdateAvailableWindow : Window
{
    private UpdateAvailableWindow(Window owner, UpdateCheckResult update)
    {
        Owner = owner;
        Title = LocalizationService.Current("SnapAnchor update available");
        Width = 530;
        Height = 390;
        MinWidth = 460;
        MinHeight = 330;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(250, 249, 247));
        Foreground = new SolidColorBrush(Color.FromRgb(28, 25, 23));
        DpiLayoutService.Attach(this);

        var root = new Grid { Margin = new Thickness(26, 22, 26, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = LocalizationService.Format("SnapAnchor {0} is available", update.Version),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        };
        root.Children.Add(title);

        var details = new TextBlock
        {
            Text = LocalizationService.Format("{0} update - Download size: {1}",
                       LocalizationService.Current(update.PackageKind == UpdatePackageKind.Portable ? "Portable" : "Installed"),
                       UpdateService.FormatBytes(update.DownloadSize)) +
                   Environment.NewLine +
                   LocalizationService.Current(update.IsSigned
                       ? "Digitally signed package"
                       : "Unsigned package - SHA-256 will be verified before it runs"),
            Foreground = new SolidColorBrush(Color.FromRgb(87, 83, 78)),
            Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(details, 1);
        root.Children.Add(details);

        var notesPanel = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(231, 229, 228)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 18)
        };
        var notesStack = new StackPanel();
        notesStack.Children.Add(new TextBlock
        {
            Text = LocalizationService.Current("What's new"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        notesStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(update.ReleaseNotes)
                ? LocalizationService.Current("See the GitHub release page for full release notes.")
                : update.ReleaseNotes,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(87, 83, 78)),
            LineHeight = 20
        });
        if (Uri.TryCreate(update.ReleasePageUrl, UriKind.Absolute, out var releaseUri))
        {
            var link = new Button
            {
                Content = LocalizationService.Current("View release on GitHub"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(41, 37, 36)),
                Padding = new Thickness(0),
                Margin = new Thickness(0, 12, 0, 0)
            };
            link.Click += (_, _) => Process.Start(new ProcessStartInfo(releaseUri.AbsoluteUri) { UseShellExecute = true });
            notesStack.Children.Add(link);
        }
        notesPanel.Child = new ScrollViewer
        {
            Content = notesStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(notesPanel, 2);
        root.Children.Add(notesPanel);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var later = new Button
        {
            Content = LocalizationService.Current("Later"),
            MinWidth = 96,
            Height = 36,
            Background = new SolidColorBrush(Color.FromRgb(231, 229, 228)),
            Foreground = new SolidColorBrush(Color.FromRgb(28, 25, 23)),
            Margin = new Thickness(0, 0, 10, 0),
            IsCancel = true
        };
        later.Click += (_, _) => DialogResult = false;
        var download = new Button
        {
            Content = LocalizationService.Current("Download update"),
            MinWidth = 132,
            Height = 36,
            Background = new SolidColorBrush(Color.FromRgb(41, 37, 36)),
            Foreground = Brushes.White,
            IsDefault = true
        };
        download.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(later);
        buttons.Children.Add(download);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);
        Content = root;
    }

    internal static bool Ask(Window owner, UpdateCheckResult update) =>
        new UpdateAvailableWindow(owner, update).ShowDialog() == true;
}
