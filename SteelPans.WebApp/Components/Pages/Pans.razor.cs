using Melanchall.DryWetMidi.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using SteelPans.WebApp.Components.Elements;
using SteelPans.WebApp.Components.Layout;
using SteelPans.WebApp.Model;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;

namespace SteelPans.WebApp.Components.Pages;

public partial class Pans : IDisposable
{
    private StartupSettings StartupSettings => StartupSettingsAccessor.Value;

    private readonly List<SteelPan> pans_ = [];
    private readonly List<MidiTrackInfo> midiTracks_ = [];
    private string? loadError_;
    private string midiFileName_ = string.Empty;
    private string mergeMidiFileName_ = string.Empty;
    private IReadOnlyList<IBrowserFile> pendingMergeMidiFiles_ = [];

    private AddPanModal? addPanModal_;
    private ModalPopup? addMergedTrackModal_;
    private ElementReference assignedPansElement_;

    private ModalPopup? removePanModal_;
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

    private async Task LoadStartupSettings()
    {
        if (!string.IsNullOrWhiteSpace(StartupSettings.MidiFile) && File.Exists(StartupSettings.MidiFile))
        {
            var fileInfo = new FileInfo(StartupSettings.MidiFile);
            midiFileName_ = fileInfo.Name;
            await OnMidiFileSelected(async () =>
            {
                await using var stream = File.OpenRead(StartupSettings.MidiFile);
                return await MidiService.OpenMidiFileAsync(stream);
            });
        }

        foreach (var startup in StartupSettings.Tracks)
        {
            var assignment = new MidiTrackAssignment
            {
                AssignedPanType = startup.Pan,
                Track = midiTracks_[startup.Track],
            };

            await Playback.OnAddAssignmentAsync(assignment, pans_);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await LoadStartupSettings();
            await JS.InvokeVoidAsync("panLayout.observe", assignedPansElement_);
        }

        await JS.InvokeVoidAsync("panLayout.update", assignedPansElement_);
    }

    private async Task OnPlaybackStateChangedAsync()
    {
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnMidiFileSelected(Func<Task<MidiFile>> getMidiFile)
    {
        await Playback.OnLoadMidiAsync(null, new Dictionary<int, List<MidiPanEvent>>());

        var midiFile = await getMidiFile();
        var playbackInfo = MidiService.GetPlaybackInfo(midiFile);
        var playableTracks = MidiService.LoadPlayableTracks(midiFile);

        midiTracks_.Clear();

        var trackEventsByIndex = new Dictionary<int, List<MidiPanEvent>>();
        foreach (var (track, events) in playableTracks)
        {
            midiTracks_.Add(track);
            trackEventsByIndex[track.Index] = events;
        }

        await Playback.OnLoadMidiAsync(playbackInfo, trackEventsByIndex);
        await InvokeAsync(StateHasChanged);
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

    public void Dispose()
    {
        Playback.StateChanged -= OnPlaybackStateChangedAsync;
    }
}
