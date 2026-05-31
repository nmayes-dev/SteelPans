using Melanchall.DryWetMidi.Core;
using SteelPans.Shared.Config;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Music;
using SteelPans.WebApp.Components.Elements;

namespace SteelPans.WebApp.Components.Pages;

public partial class Pans : IAsyncDisposable
{
    private Settings Settings => SettingsAccessor.Value;

    private IReadOnlyDictionary<GroupSummaryDto, IReadOnlyList<GroupFileDto>> groupFiles_ = new Dictionary<GroupSummaryDto, IReadOnlyList<GroupFileDto>>();

    private string loadError_ = string.Empty;



    protected override async Task OnInitializedAsync()
    {
        Playback.StateChanged += OnPlaybackStateChangedAsync;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && Settings.UseStartupConfig)
        {
            await LoadStartupFileAsync(Settings.StartupConfig.MidiFilePath);
            await LoadPanLayoutAsync(Settings.StartupConfig.Layout);
        }
    }

    private async Task LoadStartupFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        var fileInfo = new FileInfo(filePath);
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

                await Playback.OnAddAssignmentAsync(assignment);
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

    public ValueTask DisposeAsync()
    {
        Playback.StateChanged -= OnPlaybackStateChangedAsync;
        return ValueTask.CompletedTask;
    }
}
