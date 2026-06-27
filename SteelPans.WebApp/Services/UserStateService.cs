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

public sealed class ActiveMidiFile
{
    public required Guid Id { get; set; }
    public required string FileName { get; set; }
    public IReadOnlyList<MidiTrackInfo> Tracks { get; set; } = [];
    public IReadOnlyList<MidiTrackAssignment> Assignments { get; set; } = [];
    public MidiPlaybackInfo? PlaybackInfo { get; set; }

}

[Flags]
public enum StateUpdate
{
    None = 0b00000000,
    Id = 0b00000001,
    Groups = 0b00000010,
    Files = 0b000000100,

    ActiveFile = 0b00001000,
    ActiveTracks = 0b00010000,
    ActiveAssignments = 0b00100000,
}

public sealed class UserStateService : IAsyncDisposable
{
    public readonly long MaxMidiFileSize = 64L * 1024L * 1024L;

    public event Func<StateUpdate, Task>? OnRefresh;

    public Guid Id { get; private set; } = Guid.Empty;

    public IReadOnlyList<GroupData> Groups { get; private set; } = [];
    public IReadOnlyList<GroupFileDto> Files { get; private set; } = [];

    public ActiveMidiFile? ActiveMidi { get; private set; }

    public void SetActiveMidiFile(
        Guid id,
        string fileName,
        IReadOnlyList<MidiTrackInfo> tracks,
        MidiPlaybackInfo? playbackInfo)
    {
        ActiveMidi = new ActiveMidiFile
        {
            Id = id,
            FileName = fileName,
            Tracks = tracks.Select(CloneTrack).ToList(),
            PlaybackInfo = playbackInfo,
        };
    }

    public void ClearActiveMidiFile()
    {
        ActiveMidi = null;
    }

    public IReadOnlyList<MidiPanEvent> GetActiveMidiTrackEvents(Guid trackId)
    {
        return ActiveMidi?.Tracks.FirstOrDefault(x => x.Id == trackId)?.Events ?? [];
    }

    public void UpsertActiveMidiTrack(MidiTrackInfo track)
    {
        if (ActiveMidi is null)
            throw new InvalidOperationException("Trying to insert track when there is no midi file active");

        var tracks = ActiveMidi.Tracks.ToList();
        var index = tracks.FindIndex(x => x.Id == track.Id);
        var copy = CloneTrack(track);

        if (index >= 0)
            tracks[index] = copy;
        else
            tracks.Add(copy);

        ActiveMidi.Tracks = tracks.OrderBy(x => x.Index).ToList();
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

    private static bool HasChanged<T>(IEnumerable<T> current, IEnumerable<T> update)
    {
        var firstNotSecond = current.Except(update);
        var secondNotFirst = update.Except(current);

        return firstNotSecond.Any() || secondNotFirst.Any();
    }

    private class AssignmentCheck
    {
        public PanType Pan { get; set; }
        public Guid? Track { get; set; }
    }

    private static StateUpdate GetActiveFileUpdateFlag(ActiveMidiFile? currentFile, MidiFileDetailsDto? updateFile)
    {
        if (currentFile is null && updateFile is null)
            return StateUpdate.None;

        if ((currentFile is null && updateFile is not null) || (currentFile is not null && updateFile is null))
            return StateUpdate.ActiveFile;

        var current = currentFile!;
        var update = updateFile!;

        var flag = StateUpdate.None;
        if (current.Id != update.Id)
            flag |= StateUpdate.ActiveFile;


        var currentTracks = current.Tracks.Select(x => x.Id);
        var updateTracks = update.Tracks.Select(x => x.Id);
        if (HasChanged(currentTracks, updateTracks))
            flag |= StateUpdate.ActiveTracks;


        var currentAssignments = current.Assignments.Select(x => new AssignmentCheck { Pan = x.AssignedPanType, Track = x.TrackId });
        var updateAssignments = update.Assignments.Select(x => new AssignmentCheck { Pan = x.PanType, Track = x.TrackId });

        if (HasChanged(currentAssignments, updateAssignments))
            flag |= StateUpdate.ActiveAssignments;

        return flag;
    }

    private async Task RunRefreshAsync()
    {
        var id = await db_.GetUserIdAsync();

        var groups = await db_.Groups.GetMyGroupsAsync();
        var files = await db_.MidiFiles.GetMyMidiFilesAsync();

        var groupData = await Task.WhenAll(groups.Select(async group => new GroupData
        {
            Summary = group,
            Files = await db_.Groups.GetGroupFilesAsync(group.Id)
        }));

        var activeFile = ActiveMidi is not null ? await db_.MidiFiles.GetMidiFileDetailsAsync(ActiveMidi.Id) : null;

        var updateFlag = StateUpdate.None;
        updateFlag = Id != id ? StateUpdate.Id : StateUpdate.None;
        updateFlag |= HasChanged(Groups, groupData) ? StateUpdate.Groups : StateUpdate.None;
        updateFlag |= HasChanged(Files, files) ? StateUpdate.Files : StateUpdate.None;
        updateFlag |= GetActiveFileUpdateFlag(ActiveMidi, activeFile);

        Id = id;
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

        await RaiseOnRefreshAsync(updateFlag);
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

    private async Task RaiseOnRefreshAsync(StateUpdate flag)
    {
        var handlers = OnRefresh;

        if (handlers is null)
            return;

        foreach (var handler in handlers
                     .GetInvocationList()
                     .Cast<Func<StateUpdate, Task>>())
        {
            await handler(flag);
        }
    }
}