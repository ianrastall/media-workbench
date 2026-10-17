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
    /// <summary>The owning window's handle; unpackaged file pickers need it.</summary>
    public IntPtr WindowHandle { get; set; }
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
    private void UseImageSettings(object sender, RoutedEventArgs args) => ViewModel.UseImageSettingsCommand.Execute((sender as FrameworkElement)?.Tag as GeneratedImage);
    private void SearchLibraryOnEnter(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Enter && ViewModel.RefreshLibraryCommand.CanExecute(null))
        {
            ViewModel.RefreshLibraryCommand.Execute(null);
            args.Handled = true;
        }
    }
    private void OpenJobLog(object sender, RoutedEventArgs args) => ViewModel.OpenJobLogCommand.Execute((sender as FrameworkElement)?.Tag as JobSummary);
    private void UseLibraryPicture(object sender, RoutedEventArgs args) => ViewModel.UseAsStartingPicture((sender as FrameworkElement)?.Tag as string);
    private void UpscaleLibraryPicture(object sender, RoutedEventArgs args) => ViewModel.UseForUpscale((sender as FrameworkElement)?.Tag as string);
    private async void ChoosePicture(object sender, RoutedEventArgs args)
    {
        if (await PickPicture() is { } path) ViewModel.UseAsStartingPicture(path);
    }
    private async void ChooseUpscalePicture(object sender, RoutedEventArgs args)
    {
        if (await PickPicture() is { } path) ViewModel.UseForUpscale(path);
    }
    private async Task<string?> PickPicture()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary };
        foreach (string extension in GenerationSettings.PictureExtensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        return (await picker.PickSingleFileAsync())?.Path;
    }
    private void AcceptPictureDrag(object sender, DragEventArgs args)
    {
        if (args.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            args.DragUIOverride.Caption = "Start from this picture";
        }
    }
    private async void DropPicture(object sender, DragEventArgs args)
    {
        if (await DroppedPath(args) is { } path) ViewModel.UseAsStartingPicture(path);
    }
    private async void DropUpscalePicture(object sender, DragEventArgs args)
    {
        if (await DroppedPath(args) is { } path) ViewModel.UseForUpscale(path);
    }
    private static async Task<string?> DroppedPath(DragEventArgs args)
    {
        if (!args.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return null;
        var items = await args.DataView.GetStorageItemsAsync();
        return items.OfType<Windows.Storage.StorageFile>().FirstOrDefault()?.Path;
    }
    // Confirm buttons live in flyouts; close them before the long-running removal starts.
    private void ClearStorage(object sender, RoutedEventArgs args)
    {
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot)) popup.IsOpen = false;
        ViewModel.ClearStorageCommand.Execute((sender as FrameworkElement)?.Tag as string);
    }
}
