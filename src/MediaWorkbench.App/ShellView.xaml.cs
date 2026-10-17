using System.ComponentModel;
using MediaWorkbench.Core;
using MediaWorkbench.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MediaWorkbench_App;

public sealed partial class ShellView : UserControl
{
    public MainViewModel ViewModel { get; }
    public FrameworkElement TitleBarElement => Header;
    private bool _ready;
    public ShellView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += SyncNavigation;
        _ready = true;
    }
    private void Navigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_ready) ViewModel.SelectedPage = args.IsSettingsSelected ? "settings"
            : (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "home";
    }
    // Keeps the navigation pane in step when the view model changes page itself (for example, retrying a job).
    private void SyncNavigation(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.SelectedPage)) return;
        object? target = ViewModel.SelectedPage == "settings" ? Navigation.SettingsItem
            : Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag?.ToString() == ViewModel.SelectedPage);
        if (target is not null && !ReferenceEquals(Navigation.SelectedItem, target)) Navigation.SelectedItem = target;
    }
    private void RetryJob(object sender, RoutedEventArgs args) => ViewModel.RetryJobCommand.Execute((sender as FrameworkElement)?.Tag as JobSummary);
    private void OpenJobLog(object sender, RoutedEventArgs args) => ViewModel.OpenJobLogCommand.Execute((sender as FrameworkElement)?.Tag as JobSummary);
    // Confirm buttons live in flyouts; close them before the long-running removal starts.
    private void ClearStorage(object sender, RoutedEventArgs args)
    {
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot)) popup.IsOpen = false;
        ViewModel.ClearStorageCommand.Execute((sender as FrameworkElement)?.Tag as string);
    }
}
