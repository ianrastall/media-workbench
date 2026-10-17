using MediaWorkbench_App.ViewModels;
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
        _ready = true;
    }
    private void Navigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_ready) ViewModel.SelectedPage = args.IsSettingsSelected ? "settings"
            : (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "home";
    }
}
