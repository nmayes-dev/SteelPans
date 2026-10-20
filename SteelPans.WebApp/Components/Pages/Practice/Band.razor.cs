using Microsoft.AspNetCore.Components;
using SteelPans.Shared.Music;
using SteelPans.WebApp.Services;

namespace SteelPans.WebApp.Components.Pages.Practice;

public partial class Band : IAsyncDisposable
{
    [SupplyParameterFromQuery(Name = "file")]
    public Guid? FileId { get; set; }



    private bool loadingFile_;
    private bool loadedFileFromQuery_;

    protected override Task OnInitializedAsync()
    {
        MidiService.Playback.MidiFileLoaded += OnMidiFileLoadedAsync;
        MidiService.Playback.MidiFileUnloaded += OnPlaybackStateChangedAsync;
        MidiService.Playback.AssignmentsChanged += OnPlaybackStateChangedAsync;

        loadingFile_ = FileId is not null && MidiService.Playback.MidiFileId != FileId;
        return Task.CompletedTask;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || loadedFileFromQuery_ || FileId is null)
            return;

        loadedFileFromQuery_ = true;

        try
        {
            await MidiService.Playback.LoadGroupMidiFile(FileId.Value);
        }
        finally
        {
            loadingFile_ = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private Task OnMidiFileLoadedAsync(MidiEventArgs.FileLoaded args)
    {
        if (!args.Tracks.Any() || args.Assignments.Any())
            return Task.CompletedTask;

        Logger.LogInformation("Opening modal");
        return Modals.OpenAsync("TrackList");
    }

    private Task OnPlaybackStateChangedAsync<TArgs>(TArgs _)
    {
        return InvokeAsync(StateHasChanged);
    }
    private async Task OnRemovePan(MidiAssignedPan pan)
    {
        await Modals.OpenAsync("RemovePan", pan);
    }

    public ValueTask DisposeAsync()
    {
        MidiService.Playback.MidiFileLoaded -= OnMidiFileLoadedAsync;
        MidiService.Playback.MidiFileUnloaded -= OnPlaybackStateChangedAsync;
        MidiService.Playback.AssignmentsChanged -= OnPlaybackStateChangedAsync;
        return ValueTask.CompletedTask;
    }
}
