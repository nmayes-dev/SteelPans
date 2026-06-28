using Microsoft.AspNetCore.Components;
using SteelPans.Components.Toolbar;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Music;
using SteelPans.Components.Services;
using SteelPans.WebApp.Services;

namespace SteelPans.WebApp.Components.Elements;

public partial class MidiTrackList
{
    [Parameter]
    public IReadOnlyList<MidiTrackInfo>? Tracks { get; set; }

    [Parameter]
    public IReadOnlyList<GroupFileDto>? Files { get; set; }


    protected override void OnInitialized()
    {
        if (Tracks is null && Files is null)
            throw new InvalidOperationException("Must provide either track list or file list");

        MidiService.Playback.AssignmentsChanged += OnUpdateAsync;
    }

    private Task OnUpdateAsync(MidiEventArgs.AssignmentsChanged _)
    {
        if (Tracks is null)
            return Task.CompletedTask;

        return InvokeAsync(StateHasChanged);
    }

    private async Task LoadGroupMidiFileAsync(GroupFileDto file)
    {
        await MidiService.Playback.LoadGroupMidiFile(file.Id);
    }

    private async Task OnTrackButtonPressedAsync(MidiTrackInfo track)
    {
        if (IsTrackAssigned(track))
        {
            await OpenRemovePanAsync(track);
            return;

        }

        await OpenAddPanAsync(track);
    }


    private async Task OpenAddPanAsync(MidiTrackInfo track)
    {
        await Modals.OpenAsync("AddPan", track.Id, new ModalOptions { CloseOthers = false });
    }

    private async Task OpenRemovePanAsync(MidiTrackInfo track)
    {
        var pan = MidiService.Playback.ActivePans.First(a => a.Assignment.TrackId == track.Id);
        await Modals.OpenAsync("RemovePan", pan, new ModalOptions { CloseOthers = false });
    }

    private string GetMetaInfo(MidiTrackInfo track)
    {
        return $"{track.NoteCount} note{(track.NoteCount == 1 ? "" : "s")}{(IsTrackAssigned(track) ? " - Assigned" : "")}";
    }
    private bool IsTrackAssigned(MidiTrackInfo track)
    {
        return MidiService.Playback.Assignments.Any(a => a.TrackId == track.Id);
    }

    public void Dispose()
    {
        MidiService.Playback.AssignmentsChanged -= OnUpdateAsync;
    }
}
