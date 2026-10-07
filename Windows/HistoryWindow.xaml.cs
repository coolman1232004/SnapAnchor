using System.Windows;
using SnapAnchor.Services;

namespace SnapAnchor.Windows;

public partial class HistoryWindow : Window
{
    public event EventHandler? RepeatLastRequested
    {
        add => View.RepeatLastRequested += value;
        remove => View.RepeatLastRequested -= value;
    }
    public HistoryWindow() { ApplicationThemeService.EnsureResources(this); InitializeComponent(); DpiLayoutService.Attach(this); }
}
