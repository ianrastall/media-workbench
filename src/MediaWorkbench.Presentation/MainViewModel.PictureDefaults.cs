using CommunityToolkit.Mvvm.Input;
using MediaWorkbench.Core;

namespace MediaWorkbench.Presentation;

public partial class MainViewModel
{
    private sealed record PictureTuning(double Steps, double Guidance, int Sampler);
    // Session-only drafts: changing family/mode must not erase edits or alter restored job settings.
    private readonly Dictionary<string, PictureTuning> _pictureTuning = [];
    private string _tuningContext = "sd15";
    private bool _updatingModels;
    public IRelayCommand UseSuggestedPictureSettingsCommand { get; }
    public string SelectedPictureModelDescription => CurrentPictureModel.CreateDescription;
    private ModelChoice CurrentPictureModel => ModelChoices.ElementAtOrDefault(Math.Max(0, ModelIndex)) ?? BuiltInChoice;

    private void UpdatePictureSelection()
    {
        if (_updatingModels) return;
        ShowSizes();
        string next = IsVideoMode ? "video" : CurrentPictureModel.UsesXlSizes ? "sdxl" : "sd15";
        if (_tuningContext != next)
        {
            _pictureTuning[_tuningContext] = new(Steps, GuidanceScale, SamplerIndex);
            _tuningContext = next;
            if (_pictureTuning.TryGetValue(next, out var tuning))
            {
                Steps = tuning.Steps;
                GuidanceScale = tuning.Guidance;
                SamplerIndex = tuning.Sampler;
            }
            else ApplySuggestedTuning(next == "sdxl");
        }
        OnPropertyChanged(nameof(SelectedPictureModelDescription));
    }

    private void ApplySuggestedTuning(bool xl)
    {
        var suggested = PictureDefaults.ForModel(xl);
        Steps = suggested.Steps;
        GuidanceScale = suggested.GuidanceScale;
        SamplerIndex = Array.FindIndex(GenerationSettings.Samplers, item => item.Id == suggested.Sampler);
    }

    private void UseSuggestedPictureSettings()
    {
        if (IsBusy || !IsPictureMode) return;
        ApplySuggestedTuning(CurrentPictureModel.UsesXlSizes);
        Notice = "Suggested picture settings restored. Your description, shape, seed and starting picture are kept.";
    }

    private void ResetPictureTuning()
    {
        _pictureTuning.Clear();
        _tuningContext = "sd15";
        ApplySuggestedTuning(xl: false);
    }
}
