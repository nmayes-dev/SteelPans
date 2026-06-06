using Melanchall.DryWetMidi.Core;
using Microsoft.AspNetCore.Components;
using SteelPans.Shared.Config;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Music;
using SteelPans.WebApp.Components.Elements;

namespace SteelPans.WebApp.Components.Pages;

public partial class Pans : IAsyncDisposable
{

    [SupplyParameterFromQuery]
    public Guid? FileId { get; set; }

    private Settings Settings => SettingsAccessor.Value;


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


        if (Settings.UseStartupConfig)
        {
            await LoadStartupFileAsync(Settings.StartupConfig.MidiFilePath);
            await LoadPanLayoutAsync(Settings.StartupConfig.Layout);
        }

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

                await Playback.OnAddAssignmentAsync(assignment);
            }
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
        Playback.PlaybackStatusChanged -= OnPlaybackStateChangedAsync;
        return ValueTask.CompletedTask;
    }
}
