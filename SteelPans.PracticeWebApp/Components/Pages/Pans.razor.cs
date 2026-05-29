using Melanchall.DryWetMidi.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SteelPans.Components.Auth;
using SteelPans.Components.Layout;
using SteelPans.PracticeWebApp.Components.Elements;
using SteelPans.PracticeWebApp.Components.Layout;
using SteelPans.Shared.Config;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Music;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteelPans.PracticeWebApp.Components.Pages;

public partial class Pans : IAsyncDisposable
{
    private Settings Settings => SettingsAccessor.Value;

    private readonly List<SteelPan> pans_ = [];

    private IReadOnlyDictionary<GroupSummaryDto, IReadOnlyList<GroupFileDto>> groupFiles_ = new Dictionary<GroupSummaryDto, IReadOnlyList<GroupFileDto>>();

    private string? loadError_;
    private string midiFileName_ = string.Empty;

    private ControlToolbar? controlToolbar_;

    private async Task<Dictionary<GroupSummaryDto, IReadOnlyList<GroupFileDto>>> LoadGroupFilesAsync()
    {
        var files = new Dictionary<GroupSummaryDto, IReadOnlyList<GroupFileDto>>();

        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        if (!authState?.User.Identity?.IsAuthenticated ?? false)
            return files;

        var groups = await Db.Groups.GetMyGroupsAsync();
        foreach (var group in groups)
            files[group] = await Db.Groups.GetGroupFilesAsync(group.Id);

        return files;
    }

    protected override async Task OnInitializedAsync()
    {
        Playback.StateChanged += OnPlaybackStateChangedAsync;

        try
        {
            pans_.Clear();
            pans_.AddRange(await PanLoader.LoadAsync());

            groupFiles_ = await LoadGroupFilesAsync();
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

    private async Task LoadStartupFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

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

    public ValueTask DisposeAsync()
    {
        Playback.StateChanged -= OnPlaybackStateChangedAsync;
        return ValueTask.CompletedTask;
    }
}
