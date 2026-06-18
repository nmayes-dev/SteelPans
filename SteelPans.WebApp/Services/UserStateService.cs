using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using SteelPans.Shared.Data;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Music;
using SteelPans.Shared.Services;

namespace SteelPans.WebApp.Services;

public sealed class GroupData
{
    public required GroupSummaryDto Summary { get; set; }
    public IReadOnlyList<GroupFileDto> Files { get; set; } = [];

    public bool CanManage()
    {
        return (Summary.Role == GroupRole.Leader || Summary.Role == GroupRole.Admin);
    }
}

public sealed class UserStateService : IAsyncDisposable
{
    public readonly long MaxMidiFileSize = 64L * 1024L * 1024L;

    public event Func<Task>? OnRefresh;

    public Guid Id { get; private set; } = Guid.Empty;

    public IReadOnlyList<GroupData> Groups { get; private set; } = [];
    public IReadOnlyList<GroupFileDto> Files { get; private set; } = [];

    public string ActiveMidiFileName { get; private set; } = string.Empty;
    public IReadOnlyList<MidiTrackInfo> ActiveMidiTracks { get; private set; } = [];
    public MidiPlaybackInfo? ActiveMidiPlaybackInfo { get; private set; }

    public void SetActiveMidiFile(
        string fileName,
        IReadOnlyList<MidiTrackInfo> tracks,
        MidiPlaybackInfo? playbackInfo)
    {
        ActiveMidiFileName = fileName;
        ActiveMidiTracks = tracks
            .Select(CloneTrack)
            .ToList();
        ActiveMidiPlaybackInfo = playbackInfo;
    }

    public void ClearActiveMidiFile()
    {
        ActiveMidiFileName = string.Empty;
        ActiveMidiTracks = [];
        ActiveMidiPlaybackInfo = null;
    }

    public IReadOnlyList<MidiPanEvent> GetActiveMidiTrackEvents(Guid trackId)
    {
        return ActiveMidiTracks.FirstOrDefault(x => x.Id == trackId)?.Events ?? [];
    }

    public void UpsertActiveMidiTrack(MidiTrackInfo track)
    {
        var tracks = ActiveMidiTracks.ToList();
        var index = tracks.FindIndex(x => x.Id == track.Id);
        var copy = CloneTrack(track);

        if (index >= 0)
            tracks[index] = copy;
        else
            tracks.Add(copy);

        ActiveMidiTracks = tracks.OrderBy(x => x.Index).ToList();
    }

    private static MidiTrackInfo CloneTrack(MidiTrackInfo track)
    {
        return new MidiTrackInfo
        {
            Id = track.Id == Guid.Empty ? Guid.NewGuid() : track.Id,
            Index = track.Index,
            Name = track.Name,
            NoteCount = track.Events.Count > 0 ? track.Events.Count : track.NoteCount,
            PanType = track.PanType,
            TempoBpm = track.TempoBpm,
            BeatsPerBar = track.BeatsPerBar,
            BeatUnit = track.BeatUnit,
            DurationSeconds = track.DurationSeconds,
            Events = track.Events
                .OrderBy(x => x.Start)
                .ThenBy(x => x.Note.SemitoneNumber)
                .Select(x => new MidiPanEvent
                {
                    Id = x.Id == Guid.Empty ? Guid.NewGuid() : x.Id,
                    Note = x.Note,
                    Start = x.Start,
                    Duration = x.Duration
                })
                .ToList()
        };
    }

    private readonly DbService db_;
    private readonly TaskRunnerService task_;
    private readonly AppUpdatesService updates_;

    private readonly SemaphoreSlim refreshLock_ = new(1, 1);

    private bool disposed_;
    private bool refreshPending_;

    public UserStateService(
        DbService db,
        TaskRunnerService task,
        AppUpdatesService updates)
    {
        db_ = db;
        task_ = task;
        updates_ = updates;

        updates_.UserStateChanged += OnRealtimeUserStateChangedAsync;
        updates_.GroupChanged += OnRealtimeGroupChangedAsync;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed_)
            return;

        disposed_ = true;

        updates_.UserStateChanged -= OnRealtimeUserStateChangedAsync;
        updates_.GroupChanged -= OnRealtimeGroupChangedAsync;

        await updates_.DisposeAsync();
    }

    public async ValueTask RefreshAsync()
    {
        if (disposed_)
            return;

        if (!await refreshLock_.WaitAsync(0))
        {
            refreshPending_ = true;
            return;
        }

        try
        {
            do
            {
                refreshPending_ = false;

                if (disposed_)
                    return;

                await task_.RunSafe(RunRefreshAsync);
            }
            while (refreshPending_);
        }
        finally
        {
            refreshLock_.Release();
        }
    }

    private async Task RunRefreshAsync()
    {
        Id = await db_.GetUserIdAsync();

        var groups = await db_.Groups.GetMyGroupsAsync();
        var files = await db_.MidiFiles.GetMyMidiFilesAsync();

        var groupData = await Task.WhenAll(groups.Select(async group => new GroupData
        {
            Summary = group,
            Files = await db_.Groups.GetGroupFilesAsync(group.Id)
        }));

        Groups = groupData;
        Files = files;

        try
        {
            await updates_.StartAsync(
                Id,
                groups.Select(x => x.Id));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SignalR update client failed to start: {ex.Message}");
        }

        await RaiseOnRefreshAsync();
    }

    private Task OnRealtimeUserStateChangedAsync()
    {
        return RefreshAsync().AsTask();
    }

    private Task OnRealtimeGroupChangedAsync(Guid groupId)
    {
        if (Groups?.Any(x => x.Summary.Id == groupId) != true)
            return Task.CompletedTask;

        return RefreshAsync().AsTask();
    }

    private async Task RaiseOnRefreshAsync()
    {
        var handlers = OnRefresh;

        if (handlers is null)
            return;

        foreach (var handler in handlers
                     .GetInvocationList()
                     .Cast<Func<Task>>())
        {
            await handler();
        }
    }
}