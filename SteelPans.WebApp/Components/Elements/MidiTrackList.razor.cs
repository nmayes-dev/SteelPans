using Microsoft.AspNetCore.Components;
using SteelPans.Components.Toolbar;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Music;

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
    }

    private async Task LoadGroupMidiFileAsync(GroupFileDto file)
    {
        await Playback.LoadGroupMidiFile(file.Id);
    }

    private async Task OnTrackButtonPressedAsync(MidiTrackInfo track)
    {
        if (IsTrackAssigned(track))
        {
            await OpenRemovePanAsync(track);
            return;

        }

        await OpenAddPanAsync();
    }


    private async Task OpenAddPanAsync()
    {
        await Modals.OpenAsync("AddPan");
    }

    private async Task OpenRemovePanAsync(MidiTrackInfo track)
    {
        var pan = Playback.ActivePans.First(a => a.Assignment.TrackId == track.Id);
        await Modals.OpenAsync("RemovePan", pan);
    }

    private string GetMetaInfo(MidiTrackInfo track)
    {
        return $"{track.NoteCount} note{(track.NoteCount == 1 ? "" : "s")}{(IsTrackAssigned(track) ? " - Assigned" : "")}";
    }
    private bool IsTrackAssigned(MidiTrackInfo track)
    {
        return Playback.Assignments.Any(a => a.TrackId == track.Id);
    }
}
