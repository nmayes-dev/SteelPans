using Melanchall.DryWetMidi.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SteelPans.PracticeWebApp.Components.Elements;
using SteelPans.PracticeWebApp.Components.Layout;
using System.Text.Json;
using SteelPans.Shared.Music;
using SteelPans.Shared.Config;
using SteelPans.Components.Layout;
using SteelPans.Components.Auth;

namespace SteelPans.PracticeWebApp.Components.Pages;

public partial class Pans : IAsyncDisposable
{
    private const long MaxMidiFileSize = 64L * 1024L * 1024L;

    private sealed class BrowserDialogFile
    {
        public string Name { get; set; } = string.Empty;
        public long Size { get; set; }
        public IBrowserFile? File { get; set; }
    }

    private Settings Settings => SettingsAccessor.Value;

    private readonly List<SteelPan> pans_ = [];
    private string? loadError_;
    private string midiFileName_ = string.Empty;
    private string mergeMidiFileName_ = string.Empty;
    private IReadOnlyList<BrowserDialogFile> pendingMergeMidiFiles_ = [];

    private AddPanModal? addPanModal_;
    private ModalPopup? addMergedTrackModal_;

    private FileSaveModal? saveModal_;
    private string? lastFileName_;

    private ModalPopup? warningModal_;
    private Configuration? pendingLoadConfiguration_;

    private ModalPopup? removePanModal_;
    private MidiAssignedPan? panPendingRemoval_;

    private Logout? logoutModal_;

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
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && Settings.UseStartupConfig)
        {
            await LoadStartupFileAsync(Settings.StartupConfig.MidiFilePath);
            await LoadPanLayoutAsync(Settings.StartupConfig.Layout);
        }
    }

    private void NavigateToLogin()
    {
        Nav.NavigateTo("/account/login");
    }
    private void NavigateToRegister()
    {
        Nav.NavigateTo("/account/register");
    }
    private void NavigateToProfile()
    {
        Nav.NavigateTo("/account/profile");
    }

    private async Task OnLoadConfigurationFileAsync(InputFileChangeEventArgs e)
    {
        try
        {
            loadError_ = null;

            var file = e.File;

            await using var stream = file.OpenReadStream(
                maxAllowedSize: 1024 * 1024);

            var result = await JsonSerializer.DeserializeAsync<Configuration>(
                stream,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (result is null)
                return;

            if (result.Version != Settings.LayoutFileVersion)
            {
                loadError_ = "Incompatible layout file version.";
                return;
            }

            if (!string.Equals(result.MidiFile, midiFileName_, StringComparison.OrdinalIgnoreCase))
            {
                pendingLoadConfiguration_ = result;
                await warningModal_!.OpenAsync();
                return;
            }

            await LoadPanLayoutAsync(result.Layout);
        }
        catch (Exception)
        {
            loadError_ = "An error occurred loading this file.";
        }
    }

    private async Task CancelLoadConfigurationAsync()
    {
        pendingLoadConfiguration_ = null;
        await warningModal_!.RequestCloseAsync();
    }

    private async Task ConfirmLoadConfigurationAsync()
    {
        if (pendingLoadConfiguration_ is null)
            return;

        await warningModal_!.RequestCloseAsync();
        await LoadPanLayoutAsync(pendingLoadConfiguration_.Layout);
        pendingLoadConfiguration_ = null;
    }

    public async Task SaveConfigurationFileAsync(string fileName)
    {
        var configuration = new Configuration
        {
            Version = Settings.Version,
            MidiFile = midiFileName_,
            Layout = Playback.Assignments.Select(a => new ConfigurationPan
            {
                Pan = a.AssignedPanType,
                Track = a.Track?.Index ?? -1,
            }).ToList()
        };

        try
        {
            var json = JsonSerializer.Serialize(configuration, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await JS.InvokeVoidAsync(
                "fileHandling.submitDownloadForm",
                "/api/download",
                fileName,
                json,
                "application/json;charset=utf-8");

            lastFileName_ = fileName;
        }
        catch (Exception)
        {
            loadError_ = "An error occurred saving this file.";
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
            var files = new List<(string Name, Stream Stream)>();

            try
            {
                foreach (var file in pendingMergeMidiFiles_)
                {
                    if (file.File is null)
                        continue;

                    var stream = file.File.OpenReadStream(MaxMidiFileSize);
                    files.Add((file.Name, stream));
                }

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

    private async Task OnMidiFilesChangedAsync(InputFileChangeEventArgs e)
    {
        try
        {
            loadError_ = null;

            var files = e.GetMultipleFiles()
                .Select(file => new BrowserDialogFile
                {
                    Name = file.Name,
                    Size = file.Size,
                    File = file
                })
                .ToList();

            if (files.Count == 0)
                return;

            if (files.Count == 1)
            {
                await OnSingleMidiSelectedAsync(files[0]);
                return;
            }

            await OnMultipleMidiSelectedAsync(files);
        }
        catch (Exception)
        {
            loadError_ = "An error occurred loading this MIDI file.";
        }
    }

    private async Task OnMultipleMidiSelectedAsync(IReadOnlyList<BrowserDialogFile> files)
    {
        if (addMergedTrackModal_ is null)
            return;

        pendingMergeMidiFiles_ = files;
        await addMergedTrackModal_.OpenAsync();
    }

    private async Task OnSingleMidiSelectedAsync(BrowserDialogFile file)
    {
        if (file.File is null)
            return;

        midiFileName_ = file.Name;
        await OnMidiFileSelected(async () =>
        {
            await using var stream = file.File.OpenReadStream(MaxMidiFileSize);
            return await MidiService.OpenMidiFileAsync(stream);
        });
    }

    private bool IsTrackAssigned(MidiTrackInfo track)
    {
        return Playback.Assignments.Any(a => a.Track?.Index == track.Index);
    }

    private async Task OnTrackButtonPressedAsync(MidiTrackInfo track)
    {
        if (IsTrackAssigned(track))
        {
            if (removePanModal_ is null)
            {
                await Playback.OnRemoveAssignmentAsync(track.Index);
                return;
            }

            panPendingRemoval_ = Playback.ActivePans.First(a => a.Assignment.Track?.Index == track.Index);
            await removePanModal_.OpenAsync(closeOthers: false);
            return;

        }

        if (addPanModal_ is not null)
            await addPanModal_.Open(track.Index);
    }

    private string GetMetaInfo(MidiTrackInfo track)
    {
        return $"{track.NoteCount} note{(track.NoteCount == 1 ? "" : "s")}{(IsTrackAssigned(track) ? " - Assigned" : "")}";
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
        await removePanModal_.OpenAsync();
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

    private async Task OnKeyPressedAsync(KeyboardEventArgs e)
    {
        var ctrl = e.CtrlKey || e.MetaKey;
        var shift = e.ShiftKey;

        switch (e.Key)
        {
            case "a" when ctrl && !shift && addPanModal_ is not null:
                await addPanModal_.Open();
                break;
        }
    }

    public ValueTask DisposeAsync()
    {
        Playback.StateChanged -= OnPlaybackStateChangedAsync;
        return ValueTask.CompletedTask;
    }
}
