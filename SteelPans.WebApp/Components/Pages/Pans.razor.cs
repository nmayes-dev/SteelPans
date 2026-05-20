using Melanchall.DryWetMidi.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SteelPans.WebApp.Components.Elements;
using SteelPans.WebApp.Components.Layout;
using SteelPans.WebApp.Model;
using System.Text.Json;

namespace SteelPans.WebApp.Components.Pages;

public partial class Pans : IAsyncDisposable
{
    private Settings Settings => SettingsAccessor.Value;

    private readonly List<SteelPan> pans_ = [];
    private string? loadError_;
    private string midiFileName_ = string.Empty;
    private string mergeMidiFileName_ = string.Empty;
    private IReadOnlyList<IBrowserFile> pendingMergeMidiFiles_ = [];

    private AddPanModal? addPanModal_;
    private ModalPopup? addMergedTrackModal_;
    private ModalPopup? removePanModal_;

    private FileSaveModal? saveModal_;
    private string? lastFileName_;

    private ModalPopup? warningModal_;
    private Configuration? pendingLoadConfiguration_;

    private MidiAssignedPan? panPendingRemoval_;

    protected override async Task OnInitializedAsync()
    {
        Playback.StateChanged += OnPlaybackStateChangedAsync;

        try
        {
            pans_.Clear();
            pans_.AddRange(await PanLoader.LoadAsync());
        }
        catch (Exception ex)
        {
            loadError_ = ex.Message;
        }
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && Settings.UseStartupConfig)
        {
            await LoadStartupFileAsync(Settings.StartupConfig.MidiFilePath);
            await LoadPanLayoutAsync(Settings.StartupConfig.Layout);
        }
    }

    public async Task LoadConfigurationFileAsync()
    {
        try
        {
            var result = await JS.InvokeAsync<Configuration>("fileDialogs.openConfigFile");

            if (result is not null)
            {
                if (result.Version != Settings.LayoutFileVersion)
                {
                    loadError_ = "Incompatible layout file version.";
                    return;
                }

                if (result.MidiFile != midiFileName_)
                {
                    pendingLoadConfiguration_ = result;
                    await warningModal_!.Open();
                    return;
                }

                await LoadPanLayoutAsync(result.Layout);
            }
        }
        catch (Exception)
        {
            loadError_ = "An error occured loading this file.";
        }
    }

    private async Task CancelLoadConfigurationAsync()
    {
        pendingLoadConfiguration_ = null;
        await warningModal_!.RequestCloseAsync();
    }

    private async Task ConfirmLoadConfigurationAsync()
    {
        if (pendingLoadConfiguration_ is null)
            return;

        await warningModal_!.RequestCloseAsync();
        await LoadPanLayoutAsync(pendingLoadConfiguration_.Layout);
        pendingLoadConfiguration_ = null;
    }

    public async Task SaveConfigurationFileAsync(string fileName)
    {
        var configuration = new Configuration
        {
            Version = Settings.Version,
            MidiFile = midiFileName_,
            Layout = Playback.Assignments.Select(a => new ConfigurationPan
            {
                Pan = a.AssignedPanType,
                Track = a.Track?.Index ?? -1,
            }).ToList()
        };

        try
        {
            await JS.InvokeVoidAsync("fileDialogs.saveConfigFile", fileName, configuration);
            lastFileName_ = fileName;
        }
        catch (Exception)
        {
            loadError_ = "An error occured saving this file.";
        }
    }

    private async Task LoadStartupFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            loadError_ = "Startup file not found.";
            return;
        }

        var fileInfo = new FileInfo(filePath);
        midiFileName_ = fileInfo.Name;
        await OnMidiFileSelected(async () =>
        {
            await using var stream = File.OpenRead(filePath);
            return await MidiService.OpenMidiFileAsync(stream);
        });
    }

    private async Task LoadPanLayoutAsync(List<ConfigurationPan> layout)
    {
        await Playback.OnClearAssignmentsAsync();

        foreach (var pan in layout)
        {
            var track = Playback.Tracks.Where(t => t.Index == pan.Track).FirstOrDefault();
            if (track is not null)
            {
                var assignment = new MidiTrackAssignment
                {
                    AssignedPanType = pan.Pan,
                    Track = track,
                };

                await Playback.OnAddAssignmentAsync(assignment, pans_);
            }
        }
    }

    private async Task OnPlaybackStateChangedAsync()
    {
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnMidiFileSelected(Func<Task<MidiFile>> getMidiFile)
    {
        await Playback.OnLoadMidiAsync(getMidiFile);
    }

    private Task CloseMergeMidiModal()
    {
        mergeMidiFileName_ = string.Empty;
        pendingMergeMidiFiles_ = [];
        return Task.CompletedTask;
    }

    private async Task ConfirmMergeMidiAsync()
    {
        if (pendingMergeMidiFiles_.Count == 0 || addMergedTrackModal_ is null)
            return;

        midiFileName_ = $"{mergeMidiFileName_.Trim()}.mid";

        var loadTracks = async () =>
        {
            var files = pendingMergeMidiFiles_
                .Select(x => (x.Name, x.OpenReadStream(maxAllowedSize: 10 * 1024 * 1024)))
                .ToList();

            try
            {
                return await MidiService.MergeMidiTracksAsync(midiFileName_, files);
            }
            finally
            {
                foreach (var (_, stream) in files)
                {
                    await stream.DisposeAsync();
                }
            }
        };

        await OnMidiFileSelected(loadTracks);
        await addMergedTrackModal_.RequestCloseAsync();

        pendingMergeMidiFiles_ = [];
        mergeMidiFileName_ = string.Empty;
    }

    private async Task OnMultipleMidiSelectedAsync(IReadOnlyList<IBrowserFile> files)
    {
        if (addMergedTrackModal_ is null)
            return;

        pendingMergeMidiFiles_ = files;
        await addMergedTrackModal_.Open();
    }

    private async Task OnSingleMidiSelectedAsync(IBrowserFile file)
    {
        midiFileName_ = file.Name;
        await OnMidiFileSelected(async () =>
        {
            await using var stream = file.OpenReadStream(maxAllowedSize: 10 * 1024 * 1024);
            return await MidiService.OpenMidiFileAsync(stream);
        });
    }

    private async Task OnClickTrackEnabledShellChangedAsync(ChangeEventArgs e)
    {
        var enabled = e.Value switch
        {
            bool value => value,
            string value when bool.TryParse(value, out var parsed) => parsed,
            _ => Playback.ClickTrackEnabled
        };

        if (Playback.ClickTrackEnabled == enabled)
            return;

        await Playback.SetClickTrackEnabledAsync(enabled);
    }

    private async Task OpenRemovePanModal(MidiAssignedPan pan)
    {
        if (removePanModal_ is null)
            return;

        panPendingRemoval_ = pan;
        await removePanModal_.Open();
    }

    private async Task CloseRemovePanModal()
    {
        if (removePanModal_ is null)
            return;

        panPendingRemoval_ = null;
        await removePanModal_.RequestCloseAsync();
    }

    private async Task ConfirmRemovePanAsync()
    {
        if (panPendingRemoval_ is null)
            return;

        var index = panPendingRemoval_.Assignment.Track?.Index ?? -1;

        await CloseRemovePanModal();
        await Playback.OnRemoveAssignmentAsync(index);
    }

    private async Task OnKeyPressedAsync(KeyboardEventArgs e)
    {
        var ctrl = e.CtrlKey || e.MetaKey;
        var shift = e.ShiftKey;

        switch (e.Key)
        {
            case "a" when ctrl && !shift && addPanModal_ is not null:
                await addPanModal_.Open();
                break;
        }
    }

    public ValueTask DisposeAsync()
    {
        Playback.StateChanged -= OnPlaybackStateChangedAsync;
        return ValueTask.CompletedTask;
    }
}
