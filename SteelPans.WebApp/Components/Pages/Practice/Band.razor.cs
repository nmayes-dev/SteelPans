using Melanchall.DryWetMidi.Core;
using Microsoft.AspNetCore.Components;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Music;
using SteelPans.WebApp.Components.Elements;

namespace SteelPans.WebApp.Components.Pages.Practice;

public partial class Band : IAsyncDisposable
{

    [SupplyParameterFromQuery(Name = "file")]
    public Guid? FileId { get; set; }


    protected override async Task OnInitializedAsync()
    {
        MidiService.Playback.MidiFileLoaded += OnPlaybackStateChangedAsync;
        MidiService.Playback.MidiFileUnloaded += OnPlaybackStateChangedAsync;
        MidiService.Playback.AssignmentsChanged += OnPlaybackStateChangedAsync;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;


        if (FileId is not null)
        {
            await MidiService.Playback.LoadGroupMidiFile(FileId.Value);
            await InvokeAsync(StateHasChanged);
        }
    }


    private async Task OnPlaybackStateChangedAsync<TArgs>(TArgs _)
    {
        await InvokeAsync(StateHasChanged);
    }


    public ValueTask DisposeAsync()
    {
        MidiService.Playback.MidiFileLoaded -= OnPlaybackStateChangedAsync;
        MidiService.Playback.MidiFileUnloaded -= OnPlaybackStateChangedAsync;
        MidiService.Playback.AssignmentsChanged -= OnPlaybackStateChangedAsync;
        return ValueTask.CompletedTask;
    }
}
