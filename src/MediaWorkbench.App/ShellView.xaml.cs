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
        ViewModel.PropertyChanged += RedrawMask;
        ViewModel.PropertyChanged += ShowVideo;
        _ready = true;
    }
    // Create plays the latest video on a loop; a new video replaces the one playing.
    private void ShowVideo(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.VideoPreviewPath)) return;
        var previous = VideoPlayer.MediaPlayer;
        string path = ViewModel.VideoPreviewPath;
        if (path.Length == 0 || !File.Exists(path)) { VideoPlayer.SetMediaPlayer(null); previous?.Dispose(); return; }
        var player = new Windows.Media.Playback.MediaPlayer { IsLoopingEnabled = true, AutoPlay = true, IsMuted = true };
        player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
        VideoPlayer.SetMediaPlayer(player);
        previous?.Dispose();
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
    private Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? _maskBitmap;
    private Windows.Foundation.Point? _lastPaint;
    private byte[] _maskPixels = [];
    // Shows the painted cells as a translucent red layer sized to the mask's own grid.
    private void RedrawMask(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.MaskVersion)) return;
        if (ViewModel.Mask is not { } mask) { PaintOverlay.Source = _maskBitmap = null; return; }
        if (_maskBitmap is null || _maskBitmap.PixelWidth != mask.Width || _maskBitmap.PixelHeight != mask.Height)
        {
            _maskBitmap = new(mask.Width, mask.Height);
            _maskPixels = new byte[mask.Width * mask.Height * 4];
            PaintSurface.Width = mask.Width;
            PaintSurface.Height = mask.Height;
            PaintOverlay.Source = _maskBitmap;
        }
        var cells = mask.Cells;
        for (int index = 0; index < cells.Length; index++)
        {
            // Premultiplied BGRA: red at about 55% opacity where painted.
            bool painted = cells[index] != 0;
            _maskPixels[index * 4] = _maskPixels[index * 4 + 1] = painted ? (byte)32 : (byte)0;
            _maskPixels[index * 4 + 2] = _maskPixels[index * 4 + 3] = painted ? (byte)140 : (byte)0;
        }
        using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(_maskBitmap.PixelBuffer))
            stream.Write(_maskPixels);
        _maskBitmap.Invalidate();
    }
    private async void StartPainting(object sender, RoutedEventArgs args)
    {
        try
        {
            // The worker applies EXIF orientation, so the mask must follow the oriented size too.
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(ViewModel.SourceImagePath);
            using var stream = await file.OpenReadAsync();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            ViewModel.StartPainting((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        }
        catch (Exception ex) { ViewModel.Notice = "That picture could not be opened for painting: " + ex.Message; }
    }
    private Windows.Foundation.Point PaintPosition(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        var position = args.GetCurrentPoint(PaintSurface).Position;
        if (ViewModel.Mask is { } mask)
        {
            double diameter = 2 * MaskCanvas.BrushSizes[Math.Clamp(ViewModel.BrushIndex, 0, MaskCanvas.BrushSizes.Length - 1)].Radius * Math.Max(mask.Width, mask.Height);
            BrushOutline.Width = BrushOutline.Height = diameter;
            Canvas.SetLeft(BrushOutline, position.X - diameter / 2);
            Canvas.SetTop(BrushOutline, position.Y - diameter / 2);
            BrushOutline.Visibility = Visibility.Visible;
        }
        return new(position.X / PaintSurface.ActualWidth, position.Y / PaintSurface.ActualHeight);
    }
    private void PaintPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (!ViewModel.IsPainting || PaintSurface.ActualWidth <= 0) return;
        PaintSurface.CapturePointer(args.Pointer);
        var point = PaintPosition(args);
        _lastPaint = point;
        ViewModel.PaintStroke(point.X, point.Y, point.X, point.Y);
        args.Handled = true;
    }
    private void PaintMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (!ViewModel.IsPainting || PaintSurface.ActualWidth <= 0) return;
        var point = PaintPosition(args);
        // Contact, not pointer capture, decides whether a stroke continues; gestures or window activation can take capture mid-stroke.
        if (_lastPaint is not { } last || !args.GetCurrentPoint(PaintSurface).IsInContact) { _lastPaint = null; return; }
        ViewModel.PaintStroke(last.X, last.Y, point.X, point.Y);
        _lastPaint = point;
        args.Handled = true;
    }
    private void PaintReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        _lastPaint = null;
        PaintSurface.ReleasePointerCapture(args.Pointer);
    }
    private void PaintExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (_lastPaint is null) BrushOutline.Visibility = Visibility.Collapsed;
    }
    private void RetryJob(object sender, RoutedEventArgs args) => ViewModel.RetryJobCommand.Execute((sender as FrameworkElement)?.Tag as JobSummary);
    private void UseImageSettings(object sender, RoutedEventArgs args) => ViewModel.UseImageSettingsCommand.Execute((sender as FrameworkElement)?.Tag as GeneratedImage);
    private void OpenLibraryImage(object sender, RoutedEventArgs args) => ViewModel.OpenImageCommand.Execute((sender as FrameworkElement)?.Tag as string);
    private void OpenLibraryVideo(object sender, RoutedEventArgs args) => ViewModel.OpenVideoCommand.Execute((sender as FrameworkElement)?.Tag as string);
    private void RemoveXl(object sender, RoutedEventArgs args)
    {
        ClosePopups();
        ViewModel.RemoveXlCommand.Execute(null);
    }
    private void RemoveVideo(object sender, RoutedEventArgs args)
    {
        ClosePopups();
        ViewModel.RemoveVideoCommand.Execute(null);
    }
    private void CopyMcpConfiguration(object sender, RoutedEventArgs args)
    {
        var content = new Windows.ApplicationModel.DataTransfer.DataPackage();
        content.SetText(ViewModel.McpConfiguration);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(content);
        ViewModel.Notice = "MCP configuration copied. Add it to a compatible client's local server settings.";
    }
    private bool _savingImage;
    private async void SaveImageCopy(object sender, RoutedEventArgs args)
    {
        if (_savingImage || ViewModel.IsBusy || (sender as FrameworkElement)?.Tag is not GeneratedImage image) return;
        _savingImage = true;
        try
        {
            // The App SDK picker returns a path without creating/truncating a file.
            // This lets the worker validate the destination and checksum before committing anything.
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowHandle))
            {
                SuggestedStartLocation = image.IsVideo ? Microsoft.Windows.Storage.Pickers.PickerLocationId.VideosLibrary : Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
                SuggestedFileName = "MediaWorkbench-" + image.Id,
                DefaultFileExtension = image.IsVideo ? ".mp4" : ".png",
                ShowOverwritePrompt = true
            };
            if (image.IsVideo) picker.FileTypeChoices.Add("MP4 video", new List<string> { ".mp4" });
            else picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
            if (await picker.PickSaveFileAsync() is { } file)
            {
                var request = new ImageExportRequest(image.Id, file.Path);
                if (ViewModel.ExportImageCommand.CanExecute(request)) await ViewModel.ExportImageCommand.ExecuteAsync(request);
            }
        }
        catch (Exception ex) { ViewModel.Notice = "The copy could not be saved: " + ex.Message; }
        finally { _savingImage = false; }
    }
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
        ClosePopups();
        ViewModel.ClearStorageCommand.Execute((sender as FrameworkElement)?.Tag as string);
    }
    private void ClosePopups()
    {
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot)) popup.IsOpen = false;
    }
    private void RemoveModel(object sender, RoutedEventArgs args)
    {
        ClosePopups();
        ViewModel.RemoveModelCommand.Execute((sender as FrameworkElement)?.Tag as ModelChoice);
    }
    private void UseDrive(object sender, RoutedEventArgs args) => ViewModel.UseDriveCommand.Execute((sender as FrameworkElement)?.Tag as DriveChoice);
    private void RetestModel(object sender, RoutedEventArgs args) => ViewModel.RetestModelCommand.Execute((sender as FrameworkElement)?.Tag as ModelChoice);
    private async void ChooseModelFile(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads };
            // .ckpt is listed so choosing one explains why it cannot be added, instead of hiding it.
            foreach (string extension in new[] { ".safetensors", ".ckpt" }) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            if (await picker.PickSingleFileAsync() is { } file) await ViewModel.ChooseModelToImportAsync(file.Path);
        }
        catch (Exception ex) { ViewModel.Notice = "That model could not be chosen: " + ex.Message; }
    }
    private async void ChooseModelFolder(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            if (await picker.PickSingleFolderAsync() is { } folder) await ViewModel.ChooseModelToImportAsync(folder.Path);
        }
        catch (Exception ex) { ViewModel.Notice = "That model folder could not be chosen: " + ex.Message; }
    }
}
