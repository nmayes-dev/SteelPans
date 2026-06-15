using Melanchall.DryWetMidi.Core;
using Microsoft.AspNetCore.Components;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Music;
using SteelPans.WebApp.Components.Elements;

namespace SteelPans.WebApp.Components.Pages.Practice;

public partial class Pans : IAsyncDisposable
{

    [SupplyParameterFromQuery(Name = "file")]
    public Guid? FileId { get; set; }


    protected override async Task OnInitializedAsync()
    {
        Playback.MidiFileLoaded += OnPlaybackStateChangedAsync;
        Playback.MidiFileUnloaded += OnPlaybackStateChangedAsync;
        Playback.AssignmentsChanged += OnPlaybackStateChangedAsync;
        Playback.PlaybackStatusChanged += OnPlaybackStateChangedAsync;
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

    private async Task LoadStartupFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        var fileInfo = new FileInfo(filePath);
        await Playback.OnLoadMidiAsync(async () =>
        {
            await using var stream = File.OpenRead(filePath);
            return (fileInfo.Name, await MidiService.OpenMidiFileAsync(stream));
        });
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
        Playback.PlaybackStatusChanged -= OnPlaybackStateChangedAsync;
        return ValueTask.CompletedTask;
    }
}
