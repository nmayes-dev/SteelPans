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
        Playback.MidiFileLoaded += OnPlaybackStateChangedAsync;
        Playback.MidiFileUnloaded += OnPlaybackStateChangedAsync;
        Playback.AssignmentsChanged += OnPlaybackStateChangedAsync;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;


        if (FileId is not null)
        {
            await Playback.LoadGroupMidiFile(FileId.Value);
            await InvokeAsync(StateHasChanged);
        }
    }


    private async Task OnPlaybackStateChangedAsync<TArgs>(TArgs _)
    {
        await InvokeAsync(StateHasChanged);
    }


    public ValueTask DisposeAsync()
    {
        Playback.MidiFileLoaded -= OnPlaybackStateChangedAsync;
        Playback.MidiFileUnloaded -= OnPlaybackStateChangedAsync;
        Playback.AssignmentsChanged -= OnPlaybackStateChangedAsync;
        return ValueTask.CompletedTask;
    }
}
