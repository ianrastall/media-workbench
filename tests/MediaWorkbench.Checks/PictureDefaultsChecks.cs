using MediaWorkbench.Core;
using MediaWorkbench.Infrastructure;
using MediaWorkbench.Presentation;

static class PictureDefaultsChecks
{
    public static async Task RunAsync(Action<bool, string> check, string output)
    {
        var store = new SettingsStore(Path.Combine(output, "picture-defaults-profile"));
        string folder = Path.Combine(output, "picture-defaults-data");
        store.Save(new(1, folder, "image", DataRootChosen: true));
        var service = new RecordingWorkbench();
        service.Xl = service.Xl with { Status = "ready" };
        var vm = new MainViewModel(service, store, new FakeUpdates(null));
        await vm.RefreshInstallationCommand.ExecuteAsync(null);
        void Select(string id) => vm.ModelIndex = vm.ModelChoices.ToList().FindIndex(model => model.Id == id);

        check(vm.Steps == 20 && vm.GuidanceScale == 7.5 && vm.SamplerIndex == 0 && vm.SelectedPictureModelDescription.Contains("512"),
            "The smaller picture model starts with its suggested tuning and a plain description");
        vm.SizeIndex = 1;
        Select(ModelChoice.XlId);
        check(vm.Steps == 20 && vm.GuidanceScale == 5 && vm.SamplerIndex == 0 && vm.SizeIndex == 1
            && vm.SizeLabels[1].Contains("832") && vm.SelectedPictureModelDescription.Contains("1024"),
            "A first XL selection uses its own suggested prompt strength and keeps the chosen shape");
        vm.Steps = 31; vm.GuidanceScale = 6.5; vm.SamplerIndex = 2;
        await vm.RefreshInstallationCommand.ExecuteAsync(null);
        check(vm.SelectedModelId == ModelChoice.XlId && vm.Steps == 31 && vm.GuidanceScale == 6.5 && vm.SamplerIndex == 2,
            "Refreshing model status preserves an XL draft's custom tuning");
        Select(ModelChoice.BuiltInId);
        check(vm.Steps == 20 && vm.GuidanceScale == 7.5 && vm.SamplerIndex == 0, "Returning to the smaller family restores its separate draft");
        vm.Steps = 27; vm.GuidanceScale = 8; vm.SamplerIndex = 1;
        Select(ModelChoice.XlId);
        check(vm.Steps == 31 && vm.GuidanceScale == 6.5 && vm.SamplerIndex == 2, "Switching families keeps previous advanced edits in the session");
        vm.CreateKindIndex = 1;
        check(vm.Steps == 20 && vm.GuidanceScale == 7.5 && !vm.UseSuggestedPictureSettingsCommand.CanExecute(null),
            "Video starts with its existing defaults instead of inheriting XL's picture tuning");
        vm.Steps = 16; vm.GuidanceScale = 9;
        vm.CreateKindIndex = 0;
        check(vm.Steps == 31 && vm.GuidanceScale == 6.5, "Returning from video restores the picture draft");
        vm.CreateKindIndex = 1;
        check(vm.Steps == 16 && vm.GuidanceScale == 9, "Video's own advanced edits also survive mode switches");
        vm.CreateKindIndex = 0;

        // Restoring across families must choose the family first, then overwrite its defaults with exact saved tuning.
        var saved = new GenerationSettings("Saved castle", "mist", 42, 13, 4.5, 512, 768, "euler-a", Model: ModelChoice.BuiltInId);
        vm.UseImageSettingsCommand.Execute(RecordingWorkbench.Image(folder) with { Settings = saved });
        check(vm.SelectedModelId == ModelChoice.BuiltInId && vm.Steps == 13 && vm.GuidanceScale == 4.5 && vm.SamplerIndex == 2
            && vm.SizeIndex == 1 && vm.Seed == 42 && !vm.NewSeedEachTime && vm.Prompt == "Saved castle",
            "Use settings restores saved tuning exactly across model families");
        var retry = saved with { Model = ModelChoice.XlId, Width = 1216, Height = 832, Steps = 17, GuidanceScale = 3.5, Sampler = "dpmpp-2m" };
        vm.RetryJobCommand.Execute(new JobSummary("retry", "failed", "stopped", "", retry, null));
        check(vm.SelectedModelId == ModelChoice.XlId && vm.Steps == 17 && vm.GuidanceScale == 3.5 && vm.SamplerIndex == 1 && vm.SizeIndex == 2,
            "Retry restores an XL job's exact controls instead of replacing them with suggestions");

        string picture = Path.Combine(folder, "starting.png");
        File.WriteAllBytes(picture, [1, 2, 3]);
        vm.UseAsStartingPicture(picture);
        vm.StartPainting(64, 64);
        vm.PaintStroke(0.5, 0.5, 0.5, 0.5);
        var mask = vm.Mask;
        vm.PictureCountIndex = 2;
        vm.UseSuggestedPictureSettingsCommand.Execute(null);
        check(vm.Steps == 20 && vm.GuidanceScale == 5 && vm.SamplerIndex == 0 && vm.SizeIndex == 2 && vm.Seed == 42
            && !vm.NewSeedEachTime && vm.Prompt == saved.Prompt && vm.NegativePrompt == saved.NegativePrompt
            && vm.SourceImagePath == picture && vm.Mask == mask && vm.Mask?.HasPaint == true && vm.PictureCountIndex == 2,
            "Suggested settings resets only tuning and keeps the prompt, seed, shape, count, starting picture and painting");

        var added = new ModelChoice("xl-added", "My XL", "ready", "", Architecture: "sdxl");
        service.Models.Add(added);
        await vm.RefreshInstallationCommand.ExecuteAsync(null);
        vm.Steps = 24;
        Select(added.Id);
        check(vm.Steps == 24 && vm.SelectedPictureModelDescription.Contains("1024"), "Added models share their family's description and draft without resetting edits");
        service.Xl = service.Xl with { Status = "not_installed" };
        await vm.RefreshInstallationCommand.ExecuteAsync(null);
        check(vm.SelectedModelId == added.Id && vm.Steps == 24, "A changed model-list index does not reset an unchanged family");

        vm.DataRootDraft = Path.Combine(output, "picture-defaults-next-data");
        vm.SaveSettingsCommand.Execute(null);
        check(vm.Steps == 20 && vm.GuidanceScale == 7.5 && vm.SamplerIndex == 0, "Changing the working folder clears session tuning drafts");
    }
}
