using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.JSInterop;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Music;
using SteelPans.Shared.Services;
using SteelPans.WebApp.Components.Elements;
using SteelPans.WebApp.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace SteelPans.WebApp.Services;

public sealed class MidiManagerService
{
    public FileManager Files { get; private set; }
    public EditingManager Edit { get; private set; }

    public PlaybackManager Playback { get; private set; }


    public MidiManagerService(DbService db, TaskRunnerService tasks, UserStateService state, SteelPanLoaderService panLoader, NavigationManager nav, SafeJSInteropService js)
    {
        Files = new();
        Edit = new(state);
        Playback = new(Files, db, tasks, state, panLoader, nav, js);
    }

    public sealed class FileManager
    {
        public async Task<MemoryStream> OpenMidiFileAsync(Stream midiStream, CancellationToken token = default)
        {
            var buffer = new MemoryStream();

            await midiStream.CopyToAsync(buffer, token);
            buffer.Position = 0;

            return buffer;
        }

        public List<MidiTrackInfo> GetTrackInfos(MemoryStream midiFileStream)
        {
            var file = MidiFile.Read(midiFileStream);
            return file.GetTrackChunks()
                .Where(x => x.GetNotes().Count > 0)
                .Select((track, i) => new MidiTrackInfo
                {
                    Index = i + 1,
                    Name = track.Events
                        .OfType<SequenceTrackNameEvent>()
                        .FirstOrDefault()?.Text,
                    NoteCount = track.GetNotes().Count
                })
                .ToList();
        }

        public async Task<MidiFile> MergeMidiTracksAsync(string name, IEnumerable<(string, Stream)> files)
        {
            var midiFileTasks = files.Select(async x =>
            {
                var (name, midiStream) = x;
                await using var buffer = new MemoryStream();
                await midiStream.CopyToAsync(buffer);
                buffer.Position = 0;

                return MidiFile.Read(buffer);
            });

            var midiFiles = await Task.WhenAll(midiFileTasks);
            var tempoMap = midiFiles[0].GetTempoMap();

            var trackChunks = midiFiles
                .Zip(files)
                .Select(data =>
                {
                    var (midi, (name, stream)) = data;
                    var notes = midi.GetNotes().ToList();
                    var chunk = notes.ToTrackChunk();

                    chunk.Events.Insert(0, new SequenceTrackNameEvent(Path.GetFileNameWithoutExtension(name))
                    {
                        DeltaTime = 0
                    });

                    return chunk;
                })
                .ToList();

            return new MidiFile(trackChunks)
            {
                TimeDivision = midiFiles[0].TimeDivision
            };
        }


        public MidiPlaybackInfo GetPlaybackInfo(
            MidiFile midiFile,
            CancellationToken cancellationToken = default)
        {
            var tempoMap = midiFile.GetTempoMap();

            var tempoChanges = new List<MidiTempoChange>();
            var timeSignatureChanges = new List<MidiTimeSignatureChange>();

            foreach (var timedEvent in midiFile.GetTimedEvents().OrderBy(e => e.Time))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var time = ToTimeSpan(TimeConverter.ConvertTo<MetricTimeSpan>(timedEvent.Time, tempoMap));

                switch (timedEvent.Event)
                {
                    case SetTempoEvent tempoEvent:
                        tempoChanges.Add(new MidiTempoChange
                        {
                            Time = time,
                            Bpm = (int)Math.Round(60_000_000d / tempoEvent.MicrosecondsPerQuarterNote),
                        });
                        break;

                    case TimeSignatureEvent timeSignatureEvent:
                        timeSignatureChanges.Add(new MidiTimeSignatureChange
                        {
                            Time = time,
                            Numerator = timeSignatureEvent.Numerator,
                            Denominator = timeSignatureEvent.Denominator,
                        });
                        break;
                }
            }

            var initialTempo = tempoChanges.FirstOrDefault(t => t.Time == TimeSpan.Zero)
                ?? tempoChanges.FirstOrDefault()
                ?? new MidiTempoChange
                {
                    Time = TimeSpan.Zero,
                    Bpm = 120,
                };

            var initialTimeSignature = timeSignatureChanges.FirstOrDefault(t => t.Time == TimeSpan.Zero)
                ?? timeSignatureChanges.FirstOrDefault()
                ?? new MidiTimeSignatureChange
                {
                    Time = TimeSpan.Zero,
                    Numerator = 4,
                    Denominator = 4,
                };

            return new MidiPlaybackInfo
            {
                InitialBpm = initialTempo.Bpm,
                InitialBeatsPerBar = initialTimeSignature.Numerator,
                InitialBeatUnit = initialTimeSignature.Denominator,
                TempoChanges = tempoChanges,
                TimeSignatureChanges = timeSignatureChanges,
            };
        }

        public List<MidiPanEvent> LoadSingleTrack(
            MidiFile midiFile,
            int trackIndex = 0,
            CancellationToken cancellationToken = default)
        {
            var tempoMap = midiFile.GetTempoMap();

            var trackChunks = midiFile.GetTrackChunks().ToList();

            if (trackChunks.Count == 0)
                return [];

            if (trackIndex < 0 || trackIndex >= trackChunks.Count)
                throw new ArgumentOutOfRangeException(nameof(trackIndex));

            var notes = trackChunks[trackIndex]
                .GetNotes()
                .OrderBy(n => n.Time)
                .ToList();

            var events = new List<MidiPanEvent>(notes.Count);

            foreach (var midiNote in notes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var startMetric = TimeConverter.ConvertTo<MetricTimeSpan>(
                    midiNote.Time,
                    tempoMap);

                var durationMetric = LengthConverter.ConvertTo<MetricTimeSpan>(
                    midiNote.Length,
                    midiNote.Time,
                    tempoMap);

                events.Add(new MidiPanEvent
                {
                    Note = Shared.Music.Note.FromMidi((int)midiNote.NoteNumber),
                    Start = ToTimeSpan(startMetric),
                    Duration = ToTimeSpan(durationMetric),
                });
            }

            return events.Where(x => x.Duration > TimeSpan.Zero).ToList();
        }

        public List<MidiTrackInfo> LoadPlayableTracks(
            MidiFile midiFile,
            IReadOnlyList<MidiTrackSummary>? persistedTracks = null)
        {
            var tempoMap = midiFile.GetTempoMap();
            var trackChunks = midiFile.GetTrackChunks().ToList();
            var persistedTracksByDisplayOrder = persistedTracks?
                .OrderBy(x => x.Index)
                .ToList();

            var result = new List<MidiTrackInfo>();
            int trackCount = 0;

            for (var i = 0; i < trackChunks.Count; i++)
            {
                var track = trackChunks[i];
                var notes = track.GetNotes().OrderBy(n => n.Time).ToList();

                var events = new List<MidiPanEvent>(notes.Count);

                foreach (var midiNote in notes)
                {
                    var startMetric = TimeConverter.ConvertTo<MetricTimeSpan>(midiNote.Time, tempoMap);
                    var durationMetric = LengthConverter.ConvertTo<MetricTimeSpan>(
                        midiNote.Length,
                        midiNote.Time,
                        tempoMap);

                    var duration = ToTimeSpan(durationMetric);
                    if (duration <= TimeSpan.Zero)
                        continue;

                    events.Add(new MidiPanEvent
                    {
                        Note = Shared.Music.Note.FromMidi((int)midiNote.NoteNumber),
                        Start = ToTimeSpan(startMetric),
                        Duration = duration,
                    });
                }

                if (events.Count == 0)
                    continue;

                trackCount++;

                var persistedTrack = persistedTracksByDisplayOrder is not null && result.Count < persistedTracksByDisplayOrder.Count
                    ? persistedTracksByDisplayOrder[result.Count]
                    : null;

                result.Add(new MidiTrackInfo
                {
                    Id = persistedTrack?.Id is { } id && id != Guid.Empty ? id : Guid.NewGuid(),
                    Index = persistedTrack?.Index ?? trackCount,
                    Name = persistedTrack?.Name ?? track.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text,
                    NoteCount = events.Count,
                    PanType = persistedTrack?.PanType ?? PanType.None,
                    TempoBpm = persistedTrack?.TempoBpm ?? 120,
                    BeatsPerBar = persistedTrack?.BeatsPerBar ?? 4,
                    BeatUnit = persistedTrack?.BeatUnit ?? 4,
                    DurationSeconds = events.Count == 0 ? 0 : events.Max(x => x.End).TotalSeconds,
                    Events = events
                });
            }

            return result;
        }

        private static TimeSpan ToTimeSpan(MetricTimeSpan m)
            => TimeSpan.FromHours(m.Hours)
             + TimeSpan.FromMinutes(m.Minutes)
             + TimeSpan.FromSeconds(m.Seconds)
             + TimeSpan.FromMilliseconds(m.Milliseconds);

    }

    public sealed class EditingManager
    {
        private readonly UserStateService state_;

        public EditingManager(UserStateService state)
        {
            state_ = state;
        }

        public IReadOnlyList<MidiTrackInfo> Tracks => state_.ActiveMidi?.Tracks ?? [];
        public Guid FileId => state_.ActiveMidi?.Id ?? Guid.Empty;
        public string Title => state_.ActiveMidi?.FileName ?? string.Empty;
        public bool HasOpenFile => state_.ActiveMidi is not null;
        public bool HasUnsavedChanges { get; private set; }

        public async Task BeginCreateFileAsync(string? title = null, bool resetExisting = false)
        {
            var tracks = resetExisting || !HasOpenFile ? [] : Tracks.ToList();
            var assignments = resetExisting || !HasOpenFile
                ? []
                : state_.ActiveMidi?.Assignments.ToList() ?? [];
            var fileName = resetExisting || !HasOpenFile
                ? (string.IsNullOrWhiteSpace(title) ? string.Empty : title.Trim())
                : Title;

            await state_.SetActiveMidiFileAsync(
                Guid.NewGuid(),
                fileName,
                tracks,
                assignments,
                CreatePlaybackInfo(tracks));

            HasUnsavedChanges = false;
        }

        public async Task SetTitleAsync(string title)
        {
            if (!HasOpenFile)
                await BeginCreateFileAsync();

            await state_.SetActiveMidiFileNameAsync(title);
            HasUnsavedChanges = true;
        }

        public MidiTrackInfo CreateEditableTrack(Guid? trackId = null)
        {
            var track = trackId is Guid id ? GetTrack(id) : null;
            return track is null
                ? new MidiTrackInfo
                {
                    Id = trackId ?? Guid.NewGuid(),
                    Index = GetNextTrackIndex(),
                    Name = $"Track {GetNextTrackIndex() + 1}",
                    TempoBpm = 120,
                    BeatsPerBar = 4,
                    BeatUnit = 4,
                    DurationSeconds = 0,
                    Events = []
                }
                : CloneTrack(track, track.Id, track.Index);
        }

        public MidiTrackInfo CreateRecordingScratch(MidiTrackInfo source)
        {
            return CloneTrack(source, source.Id == Guid.Empty ? Guid.NewGuid() : source.Id, source.Index);
        }

        public async Task CommitEditableTrack(MidiTrackInfo track)
        {
            if (!HasOpenFile)
                await BeginCreateFileAsync();

            HasUnsavedChanges = true;
            await CreateOrUpdateTrack(track);
        }

        public void MarkSaved()
        {
            HasUnsavedChanges = false;
        }

        public MidiTrackInfo? GetTrack(Guid id)
        {
            return Tracks.FirstOrDefault(x => x.Id == id);
        }

        public async Task<MidiTrackInfo> CreateTrack(MidiTrackInfo track)
        {
            if (!HasOpenFile)
                await BeginCreateFileAsync();

            var copy = CloneTrack(track, track.Id == Guid.Empty ? Guid.NewGuid() : track.Id, GetNextTrackIndex());
            await state_.UpsertActiveMidiTrackAsync(copy);
            HasUnsavedChanges = true;
            return copy;
        }

        public async Task<bool> UpdateTrack(Guid id, MidiTrackInfo track)
        {
            var existing = GetTrack(id);
            if (existing is null)
                return false;

            var copy = CloneTrack(track, id, existing.Index);
            await state_.UpsertActiveMidiTrackAsync(copy);
            HasUnsavedChanges = true;
            return true;
        }

        public async Task CreateOrUpdateTrack(MidiTrackInfo track)
        {
            if (track.Id != Guid.Empty && GetTrack(track.Id) is not null)
                await UpdateTrack(track.Id, track);
            else
                await CreateTrack(track);
        }

        public async Task AddOrReplaceTrack(MidiTrackInfo track)
        {
            if (!HasOpenFile)
                await BeginCreateFileAsync();

            var id = track.Id == Guid.Empty ? Guid.NewGuid() : track.Id;
            var copy = CloneTrack(track, id, track.Index);
            await state_.UpsertActiveMidiTrackAsync(copy);
            HasUnsavedChanges = true;
        }

        public async Task RemoveTrackAsync(Guid id)
        {
            if (await state_.RemoveActiveMidiTrackAsync(id))
                HasUnsavedChanges = true;
        }

        public async Task ClearAsync()
        {
            HasUnsavedChanges = false;
            await state_.ClearActiveMidiFileAsync();
        }

        private int GetNextTrackIndex()
        {
            return Tracks.Count == 0 ? 0 : Tracks.Max(x => x.Index) + 1;
        }

        private static MidiPlaybackInfo? CreatePlaybackInfo(IReadOnlyList<MidiTrackInfo> tracks)
        {
            return tracks.FirstOrDefault() is { } first
                ? new MidiPlaybackInfo
                {
                    InitialBpm = first.TempoBpm,
                    InitialBeatsPerBar = first.BeatsPerBar,
                    InitialBeatUnit = first.BeatUnit
                }
                : null;
        }

        private static MidiTrackInfo CloneTrack(MidiTrackInfo track, Guid id, int index)
        {
            var events = CloneEvents(track.Events);
            return new MidiTrackInfo
            {
                Id = id,
                Index = index,
                Name = string.IsNullOrWhiteSpace(track.Name) ? $"Track {index + 1}" : track.Name.Trim(),
                NoteCount = events.Count,
                PanType = track.PanType,
                TempoBpm = track.TempoBpm,
                BeatsPerBar = track.BeatsPerBar,
                BeatUnit = track.BeatUnit,
                DurationSeconds = track.DurationSeconds,
                Events = events
            };
        }

        private static List<MidiPanEvent> CloneEvents(IEnumerable<MidiPanEvent> events)
        {
            return events
                .OrderBy(x => x.Start)
                .ThenBy(x => x.Note.SemitoneNumber)
                .Select(x => new MidiPanEvent
                {
                    Id = x.Id == Guid.Empty ? Guid.NewGuid() : x.Id,
                    Note = x.Note,
                    Start = x.Start,
                    Duration = x.Duration
                })
                .ToList();
        }
    }

    public sealed class PlaybackManager
    {
        public sealed record MetronomeScheduleAction(
            double TimeSeconds,
            bool IsAccent,
            bool IsSubdivision = false);

        public sealed record MetronomeScheduleState(
            bool IsRunning,
            int Generation,
            double StartAt,
            double AudioTime,
            double ElapsedSeconds,
            double ScoreSeconds,
            double FirstActionTimeSeconds,
            int LastActionIndex,
            MetronomeScheduleAction? LastAction);

        private sealed record ServiceMetronomeAction(
            double TimeSeconds,
            bool IsAccent,
            bool IsSubdivision = false);

        private readonly FileManager fileManager_;
        private readonly DbService db_;
        private readonly TaskRunnerService tasks_;
        private readonly UserStateService state_;
        private readonly SafeJSInteropService js_;

        private IDisposable navCallback_;

        private readonly Dictionary<Guid, SteelPanView> steelPanViews_ = [];
        private readonly HashSet<string> playingComponentIds_ = [];
        private readonly Dictionary<Guid, MidiTrackInfo> liveTrackOverrides_ = [];

        private CancellationTokenSource? midiPlaybackCts_;
        private CancellationTokenSource? playbackProgressCts_;

        private int? midiBpmOverride_;
        private double? midiStartAt_;

        private TimeSpan playbackSessionStartOffset_ = TimeSpan.Zero;
        private double? playbackAudioAnchorTime_;
        private TimeSpan playbackScoreAnchorOffset_ = TimeSpan.Zero;
        private int playbackTempoAnchorBpm_ = 120;

        public PlaybackManager(FileManager fileManager, DbService db, TaskRunnerService tasks, UserStateService state, SteelPanLoaderService panLoader, NavigationManager nav, SafeJSInteropService js)
        {
            fileManager_ = fileManager;
            db_ = db;
            tasks_ = tasks;
            state_ = state;
            js_ = js;

            AvailablePans = panLoader.Pans;

            state_.OnRefresh += OnRefreshAsync;
            navCallback_ = nav.RegisterLocationChangingHandler(OnNavigationAsync);
        }

        public event Func<MidiEventArgs.FileLoaded, Task>? MidiFileLoaded;
        public event Func<MidiEventArgs.FileUnloaded, Task>? MidiFileUnloaded;
        public event Func<MidiEventArgs.AssignmentsChanged, Task>? AssignmentsChanged;
        public event Func<MidiEventArgs.PanMixChanged, Task>? PanMixChanged;
        public event Func<MidiEventArgs.ClickTrackSettingsChanged, Task>? ClickTrackSettingsChanged;
        public event Func<MidiEventArgs.PlaybackStarted, Task>? PlaybackStarted;
        public event Func<MidiEventArgs.PlaybackPaused, Task>? PlaybackPaused;
        public event Func<MidiEventArgs.PlaybackStopped, Task>? PlaybackStopped;
        public event Func<MidiEventArgs.PlaybackPositionChanged, Task>? PositionChanged;
        public event Func<MidiEventArgs.PlaybackTempoChanged, Task>? TempoChanged;
        public event Func<MidiEventArgs.PlaybackCountInChanged, Task>? CountInChanged;

        public IReadOnlyList<SteelPan> AvailablePans { get; } = [];
        public List<MidiAssignedPan> ActivePans { get; private set; } = [];

        public bool IsMidiLoaded => state_.ActiveMidi is not null;
        public MidiPlaybackInfo? MidiPlaybackInfo => state_.ActiveMidi?.PlaybackInfo;
        public Guid MidiFileId => state_.ActiveMidi?.Id ?? Guid.Empty;
        public string MidiFileName => state_.ActiveMidi?.FileName ?? string.Empty;
        public IReadOnlyList<MidiTrackInfo> Tracks => state_.ActiveMidi?.Tracks ?? [];
        public IReadOnlyList<MidiTrackAssignment> Assignments => state_.ActiveMidi?.Assignments ?? [];
        public bool MidiPersistedFile => state_.ActiveMidi?.IsPersisted ?? false;

        public bool IsPlaying { get; private set; }
        public TimeSpan Position { get; private set; }
        public TimeSpan Duration { get; private set; }
        public double? MidiStartAt => midiStartAt_;

        public int TempoBpm { get; private set; } = 120;
        public int BeatsPerBar { get; private set; } = 4;
        public int BeatUnit { get; private set; } = 4;
        public bool ClickTrackEnabled { get; private set; }
        public int CountInBeats { get; private set; }
        public int CountInNoteDivision { get; private set; } = 4;

        public int InitialMidiBpm => MidiPlaybackInfo?.InitialBpm ?? TempoBpm;
        public int EffectiveMidiBpm => midiBpmOverride_ ?? MidiPlaybackInfo?.InitialBpm ?? TempoBpm;

        private async Task OnRefreshAsync(StateUpdate updateFlag)
        {
            if (state_.ActiveMidi is null || state_.ActiveMidi.Id == Guid.Empty || !state_.ActiveMidi.IsPersisted)
                return;

            if (!updateFlag.HasFlag(StateUpdate.ActiveAssignments))
                return;

            await ReloadMidiAssignments(state_.ActiveMidi.Id);
        }

        public IReadOnlyList<MidiPanEvent> GetMidiTrackEvents(MidiAssignedPan? assignedPan)
        {
            if (assignedPan?.Assignment?.TrackId is null)
                return [];

            var trackId = assignedPan.Assignment.TrackId.Value;
            var events = liveTrackOverrides_.TryGetValue(trackId, out var liveTrack)
                ? liveTrack.Events
                : state_.GetActiveMidiTrackEvents(trackId);

            return assignedPan.Pan.Filter(events).ToList();
        }

        public MidiAssignedPan? GetAssignedPanForTrack(Guid trackId)
        {
            return ActivePans.FirstOrDefault(x => x.Assignment.TrackId == trackId);
        }

        public MidiTrackInfo? GetTrackForAssignment(MidiTrackAssignment assignment) => Tracks.FirstOrDefault(x => x.Id == assignment.TrackId);

        private static string GetHeadlessPlaybackComponentId(Guid instanceId)
        {
            return $"midi-playback-{instanceId:N}";
        }

        private string GetPlaybackComponentId(MidiAssignedPan assignedPan)
        {
            return steelPanViews_.TryGetValue(assignedPan.InstanceId, out var view)
                ? view.ComponentId
                : GetHeadlessPlaybackComponentId(assignedPan.InstanceId);
        }

        private async Task SetPlaybackComponentVolumeAsync(MidiAssignedPan assignedPan, double volume)
        {
            var clampedVolume = Math.Clamp(volume, 0.0, 1.0);
            var headlessComponentId = GetHeadlessPlaybackComponentId(assignedPan.InstanceId);

            await js_.InvokeVoidAsync(
                "panPlayback.setComponentVolume",
                headlessComponentId,
                clampedVolume);

            if (!steelPanViews_.TryGetValue(assignedPan.InstanceId, out var view) || view.ComponentId == headlessComponentId)
                return;

            await js_.InvokeVoidAsync(
                "panPlayback.setComponentVolume",
                view.ComponentId,
                clampedVolume);
        }

        private async Task ApplyPlaybackVolumesAsync()
        {
            var anySolo = ActivePans.Any(x => x.Soloing);

            foreach (var pan in ActivePans)
            {
                var volume = anySolo
                    ? (pan.Soloing ? Math.Clamp(pan.Volume, 0.0, 1.0) : 0.0)
                    : GetEffectivePanVolume(pan);

                await SetPlaybackComponentVolumeAsync(pan, volume);
            }
        }

        public async Task OnUnloadMidiAsync()
        {
            await StopAsync(resetPosition: true);

            ActivePans.Clear();
            liveTrackOverrides_.Clear();

            steelPanViews_.Clear();
            playingComponentIds_.Clear();

            midiBpmOverride_ = null;
            midiStartAt_ = null;

            playbackSessionStartOffset_ = TimeSpan.Zero;
            playbackAudioAnchorTime_ = null;
            playbackScoreAnchorOffset_ = TimeSpan.Zero;
            playbackTempoAnchorBpm_ = 120;

            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;

            await state_.ClearActiveMidiFileAsync();

            await NotifyMidiFileUnloadedAsync();
            await NotifyAssignmentsChangedAsync(PlaybackAssignmentChangeOperation.Remove);
            await NotifyClickTrackSettingsChangedAsync();
            await NotifyPositionChangedAsync(jump: true);
            await PushPlaybackStateToJsAsync();
        }

        public async Task OnLoadMidiAsync(
            Func<Task<(Guid, string, MidiFile)>> getMidiFile)
        {
            await StopAsync(resetPosition: true);

            ActivePans.Clear();
            liveTrackOverrides_.Clear();

            steelPanViews_.Clear();
            playingComponentIds_.Clear();

            midiBpmOverride_ = null;
            midiStartAt_ = null;
            playbackSessionStartOffset_ = TimeSpan.Zero;
            playbackAudioAnchorTime_ = null;
            playbackScoreAnchorOffset_ = TimeSpan.Zero;
            playbackTempoAnchorBpm_ = 120;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;

            var (fileId, fileName, midiFile) = await getMidiFile();

            var playbackInfo = fileManager_.GetPlaybackInfo(midiFile);
            var details = await db_.MidiFiles.GetMidiFileDetailsAsync(fileId);

            var playableTracks = fileManager_.LoadPlayableTracks(
                midiFile,
                details?.Tracks?.OrderBy(x => x.TrackIndex)
                    .Select(x => new MidiTrackSummary
                    {
                        Id = x.Id,
                        Index = x.TrackIndex,
                        Name = x.TrackName,
                        PanType = x.SuggestedPanType ?? PanType.None,
                        TempoBpm = playbackInfo.InitialBpm,
                        BeatsPerBar = playbackInfo.InitialBeatsPerBar,
                        BeatUnit = playbackInfo.InitialBeatUnit
                    })
                    .ToList());

            var playableAssignments = details?.Assignments?
                .Where(x => x.PanType != PanType.None)
                .Select(x => new MidiTrackAssignment
                {
                    AssignedPanType = x.PanType,
                    TrackId = x.TrackId,
                    Label = x.Label,
                    IsSelected = false,
                }).ToList();

            await state_.SetActiveMidiFileAsync(
                fileId,
                fileName,
                playableTracks,
                playableAssignments,
                playbackInfo,
                isPersisted: details is not null);

            foreach (var assignment in Assignments)
                await OnAddAssignmentAsync(assignment, false, true);

            if (MidiPlaybackInfo is not null)
            {
                TempoBpm = MidiPlaybackInfo.InitialBpm;
                BeatsPerBar = MidiPlaybackInfo.InitialBeatsPerBar;
                BeatUnit = MidiPlaybackInfo.InitialBeatUnit;
            }

            await NotifyMidiFileLoadedAsync();
            await NotifyClickTrackSettingsChangedAsync();
            await NotifyPositionChangedAsync(jump: true);
            await PushPlaybackStateToJsAsync();
        }

        public async Task OnLoadMidiAsync(Guid fileId, string fileName, MidiFile midiFile, IReadOnlyList<MidiTrackDto> tracks, IReadOnlyList<MidiTrackAssignmentDto> assignments)
        {
            await StopAsync(resetPosition: true);

            ActivePans.Clear();
            liveTrackOverrides_.Clear();

            steelPanViews_.Clear();
            playingComponentIds_.Clear();

            midiBpmOverride_ = null;
            midiStartAt_ = null;
            playbackSessionStartOffset_ = TimeSpan.Zero;
            playbackAudioAnchorTime_ = null;
            playbackScoreAnchorOffset_ = TimeSpan.Zero;
            playbackTempoAnchorBpm_ = 120;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;

            var playbackInfo = fileManager_.GetPlaybackInfo(midiFile);
            var playableTracks = fileManager_.LoadPlayableTracks(
                midiFile,
                tracks?.OrderBy(x => x.TrackIndex)
                    .Select(x => new MidiTrackSummary
                    {
                        Id = x.Id,
                        Index = x.TrackIndex,
                        Name = x.TrackName,
                        PanType = x.SuggestedPanType ?? PanType.None,
                        TempoBpm = playbackInfo.InitialBpm,
                        BeatsPerBar = playbackInfo.InitialBeatsPerBar,
                        BeatUnit = playbackInfo.InitialBeatUnit
                    })
                    .ToList());

            var playableAssignments = assignments?
                .Where(x => x.PanType != PanType.None)
                .Select(x => new MidiTrackAssignment
                {
                    AssignedPanType = x.PanType,
                    TrackId = x.TrackId,
                    Label = x.Label,
                    IsSelected = false,
                }).ToList();

            await state_.SetActiveMidiFileAsync(fileId, fileName, playableTracks, playableAssignments, playbackInfo);
            foreach (var assignment in Assignments)
                await OnAddAssignmentAsync(assignment, false, true);

            if (MidiPlaybackInfo is not null)
            {
                TempoBpm = MidiPlaybackInfo.InitialBpm;
                BeatsPerBar = MidiPlaybackInfo.InitialBeatsPerBar;
                BeatUnit = MidiPlaybackInfo.InitialBeatUnit;
            }

            await NotifyMidiFileLoadedAsync();
            await NotifyClickTrackSettingsChangedAsync();
            await NotifyPositionChangedAsync(jump: true);
            await PushPlaybackStateToJsAsync();
        }


        public async Task SetLiveTrackOverrideAsync(MidiTrackInfo track)
        {
            if (IsPlaying)
                await StopAsync(resetPosition: false);

            liveTrackOverrides_[track.Id] = track;

            var existingPan = ActivePans.FirstOrDefault(x => x.Assignment.TrackId == track.Id);
            var panType = track.PanType != PanType.None
                ? track.PanType
                : existingPan?.Assignment.AssignedPanType ?? PanType.None;

            ActivePans.RemoveAll(x => x.Assignment.TrackId == track.Id);

            var sourcePan = AvailablePans.FirstOrDefault(x => x.Type == panType);
            if (sourcePan is not null)
            {
                ActivePans.Add(new MidiAssignedPan
                {
                    InstanceId = existingPan?.InstanceId ?? Guid.NewGuid(),
                    Assignment = new MidiTrackAssignment
                    {
                        TrackId = track.Id,
                        AssignedPanType = panType,
                        Label = track.Name,
                        IsSelected = existingPan?.Assignment.IsSelected ?? false,
                    },
                    Pan = ClonePan(sourcePan),
                    Volume = existingPan?.Volume ?? 1.0,
                    Muted = existingPan?.Muted ?? false,
                    Soloing = existingPan?.Soloing ?? false,
                });
            }

            if (MidiPlaybackInfo is null ||
                MidiPlaybackInfo.InitialBpm != track.TempoBpm ||
                MidiPlaybackInfo.InitialBeatsPerBar != track.BeatsPerBar ||
                MidiPlaybackInfo.InitialBeatUnit != track.BeatUnit)
            {
                TempoBpm = track.TempoBpm;
                BeatsPerBar = track.BeatsPerBar;
                BeatUnit = track.BeatUnit;
            }

            RecalculateDuration();
            await NotifyPositionChangedAsync(jump: true);
            await PushPlaybackStateToJsAsync();
        }

        public async Task ClearLiveTrackOverrideAsync(Guid trackId)
        {
            if (!liveTrackOverrides_.Remove(trackId))
                return;

            if (IsPlaying)
                await StopAsync(resetPosition: false);

            ActivePans.RemoveAll(x => x.Assignment.TrackId == trackId);

            var assignment = Assignments.FirstOrDefault(x => x.TrackId == trackId);
            if (assignment is not null)
                await OnAddAssignmentAsync(assignment, newAssignment: false, notify: false);
            else
                RecalculateDuration();

            await NotifyPositionChangedAsync(jump: true);
            await PushPlaybackStateToJsAsync();
        }

        public async Task PlayPreviewNoteAsync(Shared.Music.Note note, PanType panType, CancellationToken cancellationToken = default)
        {
            var componentId = $"midi-preview-{panType}";
            await js_.InvokeVoidAsync("panPlayback.playNote", cancellationToken, componentId, note.ToString());
        }

        public async Task LoadGroupMidiFile(Guid fileId)
        {
            if (MidiFileId == fileId)
                return;

            await tasks_.RunUnsafe(async () =>
            {
                var details = await db_.MidiFiles.GetMidiFileDetailsAsync(fileId);
                var download = await db_.MidiFiles.OpenMidiFileAsync(fileId);

                if (details is null || download is null)
                    throw new FileNotFoundException("MIDI file was not found.");

                await using var stream = download.Stream;
                using var buffer = await fileManager_.OpenMidiFileAsync(stream);

                await OnLoadMidiAsync(
                    fileId,
                    download.FileName,
                    MidiFile.Read(buffer),
                    details.Tracks,
                    details.Assignments);
            });
        }

        private async Task ReloadMidiAssignments(Guid fileId)
        {
            var details = await db_.MidiFiles.GetMidiFileDetailsAsync(fileId);
            if (details is null)
                return;

            await OnClearAssignmentsAsync(notify: false);

            foreach (var savedAssignment in details.Assignments.Where(x => x.PanType != PanType.None))
            {
                var track = Tracks.FirstOrDefault(x => x.Id == savedAssignment.TrackId);

                if (track is null)
                    continue;

                var assignment = new MidiTrackAssignment
                {
                    AssignedPanType = savedAssignment.PanType,
                    TrackId = track.Id
                };
                await state_.AddOrReplaceActiveMidiAssignmentAsync(assignment);
                await OnAddAssignmentAsync(assignment, newAssignment: false, notify: false);
            }

            await NotifyAssignmentsChangedAsync(PlaybackAssignmentChangeOperation.Reload);
            await NotifyPositionChangedAsync(jump: true);
            await PushPlaybackStateToJsAsync();
        }

        public async Task OnClearAssignmentsAsync(bool notify)
        {
            await StopAsync(resetPosition: true);

            await state_.ReplaceActiveMidiAssignmentsAsync([]);
            ActivePans.Clear();
            liveTrackOverrides_.Clear();

            steelPanViews_.Clear();
            playingComponentIds_.Clear();

            playbackSessionStartOffset_ = TimeSpan.Zero;
            playbackAudioAnchorTime_ = null;
            playbackScoreAnchorOffset_ = TimeSpan.Zero;
            playbackTempoAnchorBpm_ = 120;

            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;

            if (notify)
            {
                await NotifyAssignmentsChangedAsync(PlaybackAssignmentChangeOperation.Remove);
                await NotifyPositionChangedAsync(jump: true);
                await PushPlaybackStateToJsAsync();
            }
        }

        public async Task AddAssignmentAsync(MidiTrackAssignment assignment)
        {
            if (state_.ActiveMidi is not null)
                await state_.AddOrReplaceActiveMidiAssignmentAsync(assignment);

            await OnAddAssignmentAsync(assignment, true, true);
        }

        private async Task OnAddAssignmentAsync(MidiTrackAssignment assignment, bool newAssignment, bool notify)
        {
            if (assignment.TrackId is not null)
                ActivePans.RemoveAll(x => x.Assignment.TrackId == assignment.TrackId);

            var assignedPan = BuildAssignedPan(assignment, AvailablePans);
            if (assignedPan is null)
                return;

            ActivePans.Add(assignedPan);
            RecalculateDuration();

            if (newAssignment && state_.ActiveMidi?.IsPersisted == true && TryCreateDtoAssignment(assignment, out var dto))
            {
                await db_.MidiFiles.AddMidiAssignmentsAsync(MidiFileId, dto!);
            }

            if (notify)
            {
                await NotifyAssignmentsChangedAsync(PlaybackAssignmentChangeOperation.Add);
                await PushPlaybackStateToJsAsync();
            }
        }

        public async Task OnRemoveAssignmentAsync(MidiAssignedPan assignedPan)
        {
            if (assignedPan.Assignment.TrackId is Guid trackId)
                await state_.RemoveActiveMidiAssignmentsAsync(trackId);

            var toRemove = assignedPan.Assignment.TrackId is Guid assignedTrackId
                ? ActivePans.Where(x => x.Assignment.TrackId == assignedTrackId).ToList()
                : ActivePans.Where(x => x.InstanceId == assignedPan.InstanceId).ToList();

            foreach (var removedPan in toRemove)
            {
                if (steelPanViews_.TryGetValue(removedPan.InstanceId, out var view))
                    playingComponentIds_.Remove(view.ComponentId);

                playingComponentIds_.Remove(GetHeadlessPlaybackComponentId(removedPan.InstanceId));
                steelPanViews_.Remove(removedPan.InstanceId);
            }

            ActivePans = ActivePans.Except(toRemove).ToList();

            if (!ActivePans.Any())
                await StopAsync();

            RecalculateDuration();
            Position = TimeSpan.Zero;
            playbackSessionStartOffset_ = TimeSpan.Zero;

            if (state_.ActiveMidi?.IsPersisted == true)
            {
                var dto = Assignments
                    .Select(CreateDtoAssignment)
                    .Where(x => x is not null)
                    .OfType<MidiTrackAssignmentDto>()
                    .OrderBy(x => x.TrackIndex)
                    .ToList();

                await db_.MidiFiles.SaveMidiAssignmentsAsync(MidiFileId, new SaveMidiAssignmentsRequest(dto));
            }

            await NotifyAssignmentsChangedAsync(PlaybackAssignmentChangeOperation.Remove);
            await NotifyPositionChangedAsync(jump: true);
            await PushPlaybackStateToJsAsync();
        }

        private MidiTrackAssignmentDto? CreateDtoAssignment(MidiTrackAssignment assignment)
        {
            var track = GetTrackForAssignment(assignment);
            if (track is null)
                return null;

            return new MidiTrackAssignmentDto
            (
                track.Id,
                track.Index,
                assignment.AssignedPanType,
                track.TrackLabel
            );
        }

        public bool TryCreateDtoAssignment(MidiTrackAssignment assignment, out MidiTrackAssignmentDto? result)
        {
            result = CreateDtoAssignment(assignment);
            return result is not null;
        }

        public void UnregisterSteelPanView(Guid instanceId)
        {
            if (steelPanViews_.TryGetValue(instanceId, out var view))
                playingComponentIds_.Remove(view.ComponentId);

            steelPanViews_.Remove(instanceId);
        }

        public async Task RegisterSteelPanViewAsync(Guid instanceId, SteelPanView? view)
        {
            if (view is null)
            {
                steelPanViews_.Remove(instanceId);
                return;
            }

            steelPanViews_[instanceId] = view;

            var assignedPan = ActivePans.FirstOrDefault(x => x.InstanceId == instanceId);
            if (assignedPan is null)
                return;

            if (!IsPlaying)
                return;

            await ApplyPlaybackVolumesAsync();

            var headlessComponentId = GetHeadlessPlaybackComponentId(instanceId);
            if (playingComponentIds_.Contains(headlessComponentId))
                await StopMidiSequenceAsync(headlessComponentId);

            await StartPanInCurrentPlaybackAsync(assignedPan, view);
        }

        private async Task StartPanInCurrentPlaybackAsync(MidiAssignedPan assignedPan, SteelPanView view)
        {
            if (!IsPlaying || midiPlaybackCts_ is null || playbackAudioAnchorTime_ is null)
                return;

            var currentAudioTime = await GetAudioTimeAsync();

            const double scheduleLeadSeconds = 0.8;

            var startAtAudioTime = currentAudioTime + scheduleLeadSeconds;
            var startAtPosition = GetCurrentPositionAtAudioTime(startAtAudioTime);

            var playbackEvents = GetPlaybackEventsFromOffset(assignedPan, startAtPosition);

            if (playbackEvents.Count == 0)
                return;

            await view.ClearSelectionAndMidiVisualStateAsync();

            await StartMidiSequenceAsync(
                view,
                playbackEvents,
                midiPlaybackCts_.Token,
                startAtAudioTime);
        }

        public async Task SetPanVolumeAsync(MidiAssignedPan activePan, double volume)
        {
            activePan.Volume = Math.Clamp(volume, 0.0, 1.0);

            if (steelPanViews_.TryGetValue(activePan.InstanceId, out var view))
            {
                await js_.InvokeVoidAsync(
                    "panPlayback.setComponentVolume",
                    view.ComponentId,
                    GetEffectivePanVolume(activePan));
            }

            await ApplyPlaybackVolumesAsync();
            await NotifyPanMixChangedAsync();
        }

        public async Task SetPanVolumesAsync(IEnumerable<MidiAssignedPan> pans, double volume)
        {
            foreach (var activePan in pans)
                activePan.Volume = Math.Clamp(volume, 0.0, 1.0);

            await ApplyPlaybackVolumesAsync();
            await NotifyPanMixChangedAsync();
        }

        public async Task SetPanMutedAsync(MidiAssignedPan activePan, bool muted)
        {
            if (activePan.Muted == muted)
                return;

            activePan.Muted = muted;

            await ApplyPlaybackVolumesAsync();
            await NotifyPanMixChangedAsync();
        }

        public async Task SetPanSoloingAsync(MidiAssignedPan activePan, bool solo)
        {
            if (activePan.Soloing == solo)
                return;

            activePan.Soloing = solo;

            if (solo)
            {
                activePan.Muted = false;

                foreach (var pan in ActivePans)
                {
                    if (pan.InstanceId != activePan.InstanceId)
                        pan.Soloing = false;
                }
            }

            await ApplyPlaybackVolumesAsync();
            await NotifyPanMixChangedAsync();
        }

        public SteelPanView? GetInteractiveSteelPanView()
        {
            foreach (var assignedPan in ActivePans)
            {
                if (steelPanViews_.TryGetValue(assignedPan.InstanceId, out var assignedView))
                    return assignedView;
            }

            return null;
        }

        public async Task SelectChordAsync(HashSet<int> pitchClasses)
        {
            var view = GetInteractiveSteelPanView();
            if (view is null)
                return;

            await view.SelectChordAsync(pitchClasses);
        }

        public async Task PlaySelectedNotesAsync()
        {
            var view = GetInteractiveSteelPanView();
            if (view is null)
                return;

            await view.PlaySelectedNotesAsync();
        }

        public async Task ToggleAsync()
        {
            if (IsPlaying)
                await PauseAsync();
            else
                await PlayAsync(playbackSessionStartOffset_);
        }

        public async Task PlayAsync(TimeSpan startOffset)
        {
            if (ActivePans.Count == 0)
                return;

            var playbackGroups = ActivePans
                .Select(x => new
                {
                    Pan = x,
                    ComponentId = GetPlaybackComponentId(x),
                    Events = GetPlaybackEventsFromOffset(x, startOffset)
                })
                .Where(x => x.Events.Count > 0)
                .ToList();

            if (playbackGroups.Count == 0)
            {
                IsPlaying = false;
                Position = Duration;
                playbackSessionStartOffset_ = Duration;
                await PushPlaybackStateToJsAsync();
                return;
            }

            midiPlaybackCts_?.Cancel();
            midiPlaybackCts_?.Dispose();
            midiPlaybackCts_ = new CancellationTokenSource();
            var playbackToken = midiPlaybackCts_.Token;

            StopPlaybackProgressLoop();
            playbackProgressCts_ = new CancellationTokenSource();

            IsPlaying = true;
            playbackSessionStartOffset_ = ClampPlaybackTime(startOffset);
            Position = playbackSessionStartOffset_;
            midiStartAt_ = null;

            await ApplyPlaybackVolumesAsync();

            if (MidiPlaybackInfo is not null)
            {
                TempoBpm = EffectiveMidiBpm;
                BeatsPerBar = MidiPlaybackInfo.InitialBeatsPerBar;
                BeatUnit = MidiPlaybackInfo.InitialBeatUnit;
            }

            try
            {
                const double scheduleLeadSeconds = 0.8;

                var currentAudioTime = await GetAudioTimeAsync(playbackToken);
                playbackToken.ThrowIfCancellationRequested();

                var shouldCountIn = playbackSessionStartOffset_ <= TimeSpan.Zero;

                var countInDurationSeconds = shouldCountIn
                    ? GetCountInDurationSeconds()
                    : 0.0;

                var metronomeStartAt = currentAudioTime + scheduleLeadSeconds;
                var sharedStartAt = metronomeStartAt + countInDurationSeconds;

                await SchedulePlaybackMetronomeAsync(
                    playbackGroups.SelectMany(x => x.Events).OrderBy(x => x.Start).ToList(),
                    playbackSessionStartOffset_,
                    metronomeStartAt,
                    countInDurationSeconds,
                    includeCountIn: shouldCountIn,
                    includeClickTrack: ClickTrackEnabled,
                    playbackToken);

                playbackToken.ThrowIfCancellationRequested();

                foreach (var group in playbackGroups)
                {
                    if (steelPanViews_.TryGetValue(group.Pan.InstanceId, out var view))
                        await view.ClearSelectionAndMidiVisualStateAsync();
                }

                foreach (var group in playbackGroups)
                {
                    playbackToken.ThrowIfCancellationRequested();

                    var actualStartAt = await StartMidiSequenceAsync(
                        group.ComponentId,
                        group.Events,
                        playbackToken,
                        sharedStartAt);

                    midiStartAt_ ??= actualStartAt;
                }

                if (midiStartAt_ is null)
                {
                    await StopMetronomeAudioAsync();
                    IsPlaying = false;
                    await PushPlaybackStateToJsAsync();
                    return;
                }

                playbackAudioAnchorTime_ = midiStartAt_.Value;
                playbackScoreAnchorOffset_ = playbackSessionStartOffset_;
                playbackTempoAnchorBpm_ = EffectiveMidiBpm;

                await NotifyPlaybackStartedAsync(new MidiEventArgs.PlaybackStarted(
                    midiStartAt_.Value,
                    playbackSessionStartOffset_,
                    playbackGroups.SelectMany(x => x.Events).OrderBy(x => x.Start).ToList()));

                StartPlaybackProgressLoop(playbackProgressCts_.Token);
                await PushPlaybackStateToJsAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async Task PauseAsync()
        {
            playbackSessionStartOffset_ = await GetCurrentPositionAsync();
            await StopAsync(resetPosition: false);
            Position = playbackSessionStartOffset_;
            await NotifyPlaybackPausedAsync(new MidiEventArgs.PlaybackPaused(Position));
            await PushPlaybackStateToJsAsync();
        }

        public async Task RestartFromAsync(TimeSpan startOffset)
        {
            await StopAsync(resetPosition: false);
            await PlayAsync(startOffset);
        }

        public async Task StopAsync(bool resetPosition = false)
        {
            if (IsPlaying)
            {
                await StopMetronomeAudioAsync();

                midiPlaybackCts_?.Cancel();
                midiPlaybackCts_?.Dispose();
                midiPlaybackCts_ = null;

                StopPlaybackProgressLoop();

                var stopTasks = playingComponentIds_.Select(StopMidiSequenceAsync);
                var clearTasks = steelPanViews_.Values.Select(x => x.ClearMidiVisualStateAsync());

                await Task.WhenAll(Enumerable.Concat(stopTasks, clearTasks));

                playingComponentIds_.Clear();
            }

            midiStartAt_ = null;
            IsPlaying = false;
            playbackAudioAnchorTime_ = null;
            playbackScoreAnchorOffset_ = Position;
            playbackTempoAnchorBpm_ = EffectiveMidiBpm;

            if (resetPosition)
            {
                Position = TimeSpan.Zero;
                playbackSessionStartOffset_ = TimeSpan.Zero;
                playbackScoreAnchorOffset_ = TimeSpan.Zero;
            }
            else
            {
                playbackSessionStartOffset_ = Position;
            }

            await NotifyPlaybackStoppedAsync(new MidiEventArgs.PlaybackStopped(resetPosition, Position));
            await PushPlaybackStateToJsAsync();
        }

        public async Task SetTempoBpmAsync(int bpm)
        {
            bpm = Math.Clamp(bpm, 20, 200);

            if (TempoBpm == bpm)
                return;

            TempoBpm = bpm;

            if (MidiPlaybackInfo is not null)
                midiBpmOverride_ = bpm;

            if (IsPlaying)
            {
                var currentPosition = await GetCurrentPositionAsync();
                var currentAudioTime = await GetAudioTimeAsync();

                Position = currentPosition;
                playbackScoreAnchorOffset_ = currentPosition;
                playbackAudioAnchorTime_ = currentAudioTime;
                playbackTempoAnchorBpm_ = bpm;

                foreach (var componentId in playingComponentIds_)
                    await js_.InvokeVoidAsync("panPlayback.updateMidiTempo", componentId, bpm);

                await RescheduleClickTrackAsync(currentPosition, currentAudioTime);
                await NotifyTempoChangedAsync(new MidiEventArgs.PlaybackTempoChanged(bpm));
            }
            else
            {
                await NotifyTempoChangedAsync(new MidiEventArgs.PlaybackTempoChanged(bpm));
            }

            await NotifyClickTrackSettingsChangedAsync();
            await PushPlaybackStateToJsAsync();
        }

        public async Task SetBeatsPerBarAsync(int beatsPerBar)
        {
            beatsPerBar = Math.Clamp(beatsPerBar, 1, 32);

            if (BeatsPerBar == beatsPerBar)
                return;

            BeatsPerBar = beatsPerBar;

            if (IsPlaying && ClickTrackEnabled)
                await RescheduleClickTrackAsync();

            await NotifyClickTrackSettingsChangedAsync();
        }

        public async Task SetBeatUnitAsync(int beatUnit)
        {
            beatUnit = beatUnit is 1 or 2 or 4 or 8 or 16 or 32
                ? beatUnit
                : 4;

            if (BeatUnit == beatUnit)
                return;

            BeatUnit = beatUnit;

            if (IsPlaying && ClickTrackEnabled)
                await RescheduleClickTrackAsync();

            await NotifyClickTrackSettingsChangedAsync();
        }

        public async Task SetClickTrackEnabledAsync(bool enabled)
        {
            if (ClickTrackEnabled == enabled)
                return;

            ClickTrackEnabled = enabled;

            if (IsPlaying)
            {
                await StopMetronomeAudioAsync();

                if (ClickTrackEnabled)
                    await RescheduleClickTrackAsync();
            }

            await NotifyClickTrackSettingsChangedAsync();
            await PushPlaybackStateToJsAsync();
        }

        public async Task SetCountInBeatsAsync(int beats)
        {
            beats = Math.Clamp(beats, 0, 32);

            if (CountInBeats == beats)
                return;

            CountInBeats = beats;
            await NotifyCountInChangedAsync(new MidiEventArgs.PlaybackCountInChanged(CountInBeats, CountInNoteDivision));
            await PushPlaybackStateToJsAsync();
        }

        public async Task SetCountInNoteDivisionAsync(int noteDivision)
        {
            noteDivision = noteDivision switch
            {
                2 or 4 or 8 or 16 => noteDivision,
                _ => 4,
            };

            if (CountInNoteDivision == noteDivision)
                return;

            CountInNoteDivision = noteDivision;
            await NotifyCountInChangedAsync(new MidiEventArgs.PlaybackCountInChanged(CountInBeats, CountInNoteDivision));
            await PushPlaybackStateToJsAsync();
        }

        public async Task SeekToStartAsync()
        {
            Position = TimeSpan.Zero;
            playbackSessionStartOffset_ = TimeSpan.Zero;

            if (IsPlaying)
                await RestartFromAsync(TimeSpan.Zero);
            else
                await PushPlaybackStateToJsAsync();

            await NotifyPositionChangedAsync(jump: true);
        }

        public async Task GoToEndAsync()
        {
            if (IsPlaying)
                await StopAsync();

            Position = Duration;
            playbackSessionStartOffset_ = Duration;

            await NotifyPositionChangedAsync(jump: true);
            await PushPlaybackStateToJsAsync();
        }

        public async Task CommitSeekAsync(TimeSpan seekTime)
        {
            var clamped = ClampPlaybackTime(seekTime);

            Position = clamped;
            playbackSessionStartOffset_ = clamped;

            if (IsPlaying)
                await RestartFromAsync(clamped);
            else
                await PushPlaybackStateToJsAsync();

            await NotifyPositionChangedAsync(jump: true);
        }

        public async Task PreviewSeekAsync(TimeSpan previewTime)
        {
            Position = ClampPlaybackTime(previewTime);
            await NotifyPositionChangedAsync(jump: true);
        }


        public async Task<double> GetAudioTimeAsync(CancellationToken cancellationToken = default)
        {
            return await js_.InvokeAsync<double>("panPlayback.getAudioTime", cancellationToken);
        }

        public async Task BeginMetronomeWeightDragAsync(ElementReference trackElement, object dotNetRef, double initialClientY)
        {
            await js_.InvokeVoidAsync("panPlayback.beginMetronomeWeightDrag", trackElement, dotNetRef, initialClientY);
        }

        public async Task PlayMetronomeTickAsync(bool isAccent, CancellationToken cancellationToken = default)
        {
            await js_.InvokeVoidAsync("panPlayback.playMetronomeTick", cancellationToken, isAccent);
        }

        public async Task StopMetronomeAudioAsync()
        {
            await js_.InvokeVoidAsync("panPlayback.stopMetronome");
        }

        public async Task<MetronomeScheduleState?> GetMetronomeScheduleStateAsync(CancellationToken cancellationToken = default)
        {
            return await js_.InvokeAsync<MetronomeScheduleState?>(
                "panPlayback.getMetronomeScheduleState",
                cancellationToken);
        }

        private async ValueTask OnNavigationAsync(LocationChangingContext ctx)
        {
            await StopAsync(resetPosition: true);
        }

        private async Task<double?> StartMidiSequenceAsync(
            SteelPanView view,
            IReadOnlyList<MidiPanEvent> events,
            CancellationToken cancellationToken = default,
            double? startAt = null)
        {
            return await StartMidiSequenceAsync(
                view.ComponentId,
                events,
                cancellationToken,
                startAt);
        }

        private async Task<double?> StartMidiSequenceAsync(
            string componentId,
            IReadOnlyList<MidiPanEvent> events,
            CancellationToken cancellationToken = default,
            double? startAt = null)
        {
            if (events.Count == 0)
                return null;

            var playbackActions = BuildPlaybackActions(events);
            if (playbackActions.Count == 0)
                return null;

            var scheduledActions = BuildScheduledActions(playbackActions);

            cancellationToken.ThrowIfCancellationRequested();

            var actualStartAt = await js_.InvokeAsync<double?>(
                "panPlayback.playMidiSchedule",
                cancellationToken,
                componentId,
                scheduledActions,
                InitialMidiBpm,
                EffectiveMidiBpm,
                startAt);

            cancellationToken.ThrowIfCancellationRequested();

            if (actualStartAt is not null)
                playingComponentIds_.Add(componentId);

            return actualStartAt;
        }

        private async Task StopMidiSequenceAsync(string componentId)
        {
            if (!playingComponentIds_.Remove(componentId))
                return;

            await js_.InvokeVoidAsync("panPlayback.stopMidiSchedule", componentId);

            var view = steelPanViews_.Values.FirstOrDefault(x => x.ComponentId == componentId);
            if (view is not null)
                await view.ClearMidiVisualStateAsync();
        }


        private async Task RescheduleClickTrackAsync(TimeSpan? currentPosition = null, double? currentAudioTime = null)
        {
            await StopMetronomeAudioAsync();

            if (!IsPlaying || !ClickTrackEnabled)
                return;

            var audioTime = currentAudioTime ?? await GetAudioTimeAsync();
            var position = currentPosition ?? GetCurrentPositionAtAudioTime(audioTime);

            await SchedulePlaybackMetronomeAsync(
                GetRemainingPlaybackEvents(position),
                position,
                audioTime + 0.05,
                countInDurationSeconds: 0.0,
                includeCountIn: false,
                includeClickTrack: true);
        }

        private IReadOnlyList<MidiPanEvent> GetRemainingPlaybackEvents(TimeSpan playbackOffset)
        {
            return ActivePans
                .SelectMany(x => GetPlaybackEventsFromOffset(x, playbackOffset))
                .OrderBy(x => x.Start)
                .ToList();
        }

        private async Task SchedulePlaybackMetronomeAsync(
            IReadOnlyList<MidiPanEvent> playbackEvents,
            TimeSpan playbackOffset,
            double startAt,
            double countInDurationSeconds,
            bool includeCountIn,
            bool includeClickTrack,
            CancellationToken cancellationToken = default)
        {
            var actions = new List<ServiceMetronomeAction>();

            if (includeCountIn && CountInBeats > 0 && countInDurationSeconds > 0.0)
                actions.AddRange(BuildCountInActions(countInDurationSeconds));

            if (includeClickTrack)
            {
                actions.AddRange(BuildClickTrackActions(
                        playbackEvents,
                        TempoBpm,
                        BeatsPerBar,
                        BeatUnit,
                        playbackOffset)
                    .Select(action => new ServiceMetronomeAction(
                        action.TimeSeconds + countInDurationSeconds,
                        action.IsAccent)));
            }

            if (actions.Count == 0)
                return;

            await js_.InvokeVoidAsync(
                "panPlayback.playMetronomeSchedule",
                cancellationToken,
                actions.OrderBy(x => x.TimeSeconds).ToList(),
                startAt,
                playbackOffset.TotalSeconds - countInDurationSeconds);
        }

        private List<ServiceMetronomeAction> BuildCountInActions(double countInDurationSeconds)
        {
            var actions = new List<ServiceMetronomeAction>();

            if (CountInBeats <= 0 || CountInNoteDivision <= 0 || TempoBpm <= 0 || BeatsPerBar <= 0 || BeatUnit <= 0)
                return actions;

            var secondsPerCountInNote = (60.0 / TempoBpm) * (4.0 / CountInNoteDivision);
            var secondsPerBeat = (60.0 / TempoBpm) * (4.0 / BeatUnit);
            var secondsPerBar = BeatsPerBar * secondsPerBeat;

            if (secondsPerCountInNote <= 0.0 || secondsPerBeat <= 0.0 || secondsPerBar <= 0.0)
                return actions;

            const double accentEpsilon = 0.000001;
            const double beatEpsilon = 0.000001;

            for (var i = 0; i < CountInBeats; i++)
            {
                var secondsFromPlaybackStart = (i - CountInBeats) * secondsPerCountInNote;
                var barPhase = PositiveModulo(secondsFromPlaybackStart, secondsPerBar);
                var beatPhase = PositiveModulo(secondsFromPlaybackStart, secondsPerBeat);

                var isAccent = barPhase <= accentEpsilon || Math.Abs(barPhase - secondsPerBar) <= accentEpsilon;
                var isBeat = beatPhase <= beatEpsilon || Math.Abs(beatPhase - secondsPerBeat) <= beatEpsilon;

                actions.Add(new ServiceMetronomeAction(
                    i * secondsPerCountInNote,
                    isAccent,
                    IsSubdivision: !isBeat));
            }

            return actions;
        }

        private static double PositiveModulo(double value, double modulus)
        {
            if (modulus <= 0.0)
                return 0.0;

            var result = value % modulus;
            return result < 0.0 ? result + modulus : result;
        }

        private static List<MidiPanPlaybackAction> BuildPlaybackActions(IEnumerable<MidiPanEvent> events)
        {
            return events
                .SelectMany(e => new[]
                {
                new MidiPanPlaybackAction
                {
                    Note = e.Note,
                    Time = e.Start,
                    IsNoteOn = true,
                },
                new MidiPanPlaybackAction
                {
                    Note = e.Note,
                    Time = e.End,
                    IsNoteOn = false,
                },
                })
                .OrderBy(a => a.Time)
                .ThenBy(a => a.IsNoteOn ? 1 : 0)
                .ToList();
        }

        private static List<MidiPanScheduledAction> BuildScheduledActions(IEnumerable<MidiPanPlaybackAction> actions)
        {
            return actions
                .Select(a => new MidiPanScheduledAction
                {
                    NoteKey = a.Note.ToString(),
                    TimeSeconds = a.Time.TotalSeconds,
                    IsNoteOn = a.IsNoteOn,
                })
                .ToList();
        }

        private MidiAssignedPan? BuildAssignedPan(MidiTrackAssignment assignment, IReadOnlyList<SteelPan> availablePans)
        {
            var sourcePan = availablePans.FirstOrDefault(x => x.Type == assignment.AssignedPanType);
            if (sourcePan is null)
                return null;

            return new MidiAssignedPan
            {
                InstanceId = Guid.NewGuid(),
                Assignment = assignment,
                Pan = ClonePan(sourcePan),
            };
        }

        private double GetPlaybackTempoRatio(int bpm)
        {
            var sourceBpm = MidiPlaybackInfo?.InitialBpm ?? bpm;
            if (sourceBpm <= 0 || bpm <= 0)
                return 1.0;

            return (double)bpm / sourceBpm;
        }

        private IReadOnlyList<MidiPanEvent> GetPlaybackEventsFromOffset(MidiAssignedPan assignedPan, TimeSpan startOffset)
        {
            var sourceEvents = GetMidiTrackEvents(assignedPan);
            var playbackEvents = sourceEvents.OrderBy(x => x.Start).ToList();
            var clampedOffset = ClampPlaybackTime(startOffset);

            if (clampedOffset <= TimeSpan.Zero)
                return playbackEvents;

            return playbackEvents
                .Where(e => e.Start + e.Duration > clampedOffset)
                .Select(e =>
                {
                    var effectiveStart = e.Start - clampedOffset;

                    if (effectiveStart < TimeSpan.Zero)
                    {
                        var trim = clampedOffset - e.Start;
                        var remainingDuration = e.Duration - trim;

                        if (remainingDuration <= TimeSpan.Zero)
                            remainingDuration = TimeSpan.FromMilliseconds(1);

                        return new MidiPanEvent
                        {
                            Note = e.Note,
                            Start = TimeSpan.Zero,
                            Duration = remainingDuration,
                        };
                    }

                    return new MidiPanEvent
                    {
                        Note = e.Note,
                        Start = effectiveStart,
                        Duration = e.Duration,
                    };
                })
                .ToList();
        }

        public static List<MetronomeAction> BuildClickTrackActions(
            IReadOnlyList<MidiPanEvent> playbackEvents,
            int bpm,
            int beatsPerBar,
            int beatUnit,
            TimeSpan playbackOffset)
        {
            var actions = new List<MetronomeAction>();

            if (playbackEvents.Count == 0 || bpm <= 0 || beatsPerBar <= 0 || beatUnit <= 0)
                return actions;

            var secondsPerBeat = (60.0 / bpm) * (4.0 / beatUnit);
            if (secondsPerBeat <= 0.0)
                return actions;

            var offsetSeconds = Math.Max(0.0, playbackOffset.TotalSeconds);
            var absoluteBeatPosition = offsetSeconds / secondsPerBeat;
            var completedBeats = (int)Math.Floor(absoluteBeatPosition);
            var beatPhase = absoluteBeatPosition - completedBeats;

            var firstActionDelay = beatPhase <= 0.000001
                ? 0.0
                : (1.0 - beatPhase) * secondsPerBeat;

            var firstAbsoluteBeatIndex = beatPhase <= 0.000001
                ? completedBeats
                : completedBeats + 1;

            var remainingDurationSeconds = playbackEvents.Max(e => e.Start + e.Duration).TotalSeconds;

            if (remainingDurationSeconds < firstActionDelay)
                return actions;

            var remainingAfterFirstBeat = remainingDurationSeconds - firstActionDelay;
            var beatCount = 1 + (int)Math.Floor(remainingAfterFirstBeat / secondsPerBeat);

            for (var i = 0; i < beatCount; i++)
            {
                var absoluteBeatIndex = firstAbsoluteBeatIndex + i;

                actions.Add(new MetronomeAction
                {
                    TimeSeconds = firstActionDelay + (i * secondsPerBeat),
                    IsAccent = absoluteBeatIndex % beatsPerBar == 0,
                });
            }

            return actions;
        }

        private double GetCountInDurationSeconds()
        {
            if (CountInBeats <= 0 || TempoBpm <= 0 || CountInNoteDivision <= 0)
                return 0.0;

            var secondsPerNote = (60.0 / TempoBpm) * (4.0 / CountInNoteDivision);
            return Math.Max(0.0, CountInBeats * secondsPerNote);
        }

        private TimeSpan GetCurrentPositionAtAudioTime(double audioTime, TimeSpan? baseOffset = null)
        {
            if (!IsPlaying || playbackAudioAnchorTime_ is null)
                return ClampPlaybackTime(baseOffset ?? playbackSessionStartOffset_);

            var elapsedAudioSeconds = Math.Max(0, audioTime - playbackAudioAnchorTime_.Value);
            var elapsedScoreSeconds = elapsedAudioSeconds * GetPlaybackTempoRatio(playbackTempoAnchorBpm_);

            return ClampPlaybackTime(playbackScoreAnchorOffset_ + TimeSpan.FromSeconds(elapsedScoreSeconds));
        }

        private async Task<TimeSpan> GetCurrentPositionAsync(TimeSpan? baseOffset = null)
        {
            var currentAudioTime = await GetAudioTimeAsync();
            return GetCurrentPositionAtAudioTime(currentAudioTime, baseOffset);
        }

        private TimeSpan ClampPlaybackTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero)
                return TimeSpan.Zero;

            if (Duration > TimeSpan.Zero && time > Duration)
                return Duration;

            return time;
        }

        private void StartPlaybackProgressLoop(CancellationToken cancellationToken)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));

                    while (IsPlaying && await timer.WaitForNextTickAsync(cancellationToken))
                    {
                        Position = await GetCurrentPositionAsync();
                        await NotifyPositionChangedAsync(jump: false);

                        if (Duration > TimeSpan.Zero && Position >= Duration)
                        {
                            IsPlaying = false;
                            playbackSessionStartOffset_ = Duration;
                            playbackAudioAnchorTime_ = null;
                            midiStartAt_ = null;

                            await NotifyPlaybackStoppedAsync(new MidiEventArgs.PlaybackStopped(false, Position));
                            await NotifyPositionChangedAsync(jump: false);
                            await PushPlaybackStateToJsAsync();

                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, cancellationToken);
        }

        private void StopPlaybackProgressLoop()
        {
            playbackProgressCts_?.Cancel();
            playbackProgressCts_?.Dispose();
            playbackProgressCts_ = null;
        }

        private void RecalculateDuration()
        {
            var maxEnd = ActivePans
                .SelectMany(x => GetMidiTrackEvents(x))
                .DefaultIfEmpty()
                .Max(x => x is null ? TimeSpan.Zero : x.Start + x.Duration);

            Duration = maxEnd;
            Position = ClampPlaybackTime(Position);
            playbackSessionStartOffset_ = ClampPlaybackTime(playbackSessionStartOffset_);
            playbackScoreAnchorOffset_ = ClampPlaybackTime(playbackScoreAnchorOffset_);
        }

        private async Task NotifyCountInChangedAsync(MidiEventArgs.PlaybackCountInChanged args)
        {
            var handlers = CountInChanged;
            if (handlers is null)
                return;

            foreach (Func<MidiEventArgs.PlaybackCountInChanged, Task> handler in handlers.GetInvocationList())
                await handler(args);
        }

        private async Task NotifyPlaybackStartedAsync(MidiEventArgs.PlaybackStarted args)
        {
            var handlers = PlaybackStarted;
            if (handlers is null)
                return;

            foreach (Func<MidiEventArgs.PlaybackStarted, Task> handler in handlers.GetInvocationList())
                await handler(args);
        }

        private async Task NotifyPlaybackPausedAsync(MidiEventArgs.PlaybackPaused args)
        {
            var handlers = PlaybackPaused;
            if (handlers is null)
                return;

            foreach (Func<MidiEventArgs.PlaybackPaused, Task> handler in handlers.GetInvocationList())
                await handler(args);
        }

        private async Task NotifyPlaybackStoppedAsync(MidiEventArgs.PlaybackStopped args)
        {
            var handlers = PlaybackStopped;
            if (handlers is null)
                return;

            foreach (Func<MidiEventArgs.PlaybackStopped, Task> handler in handlers.GetInvocationList())
                await handler(args);
        }

        private async Task NotifyPositionChangedAsync(bool jump)
        {
            var handlers = PositionChanged;
            if (handlers is null)
                return;

            var args = new MidiEventArgs.PlaybackPositionChanged(Position, Duration, IsPlaying, !jump);

            foreach (Func<MidiEventArgs.PlaybackPositionChanged, Task> handler in handlers.GetInvocationList())
                await handler(args);
        }

        private async Task NotifyTempoChangedAsync(MidiEventArgs.PlaybackTempoChanged args)
        {
            var handlers = TempoChanged;
            if (handlers is null)
                return;

            foreach (Func<MidiEventArgs.PlaybackTempoChanged, Task> handler in handlers.GetInvocationList())
                await handler(args);
        }

        private async Task NotifyMidiFileLoadedAsync()
        {
            var handlers = MidiFileLoaded;
            if (handlers is null)
                return;

            var args = new MidiEventArgs.FileLoaded(
                MidiFileId,
                MidiFileName,
                Tracks.ToList(),
                InitialMidiBpm,
                BeatsPerBar,
                BeatUnit,
                MidiPersistedFile);

            foreach (Func<MidiEventArgs.FileLoaded, Task> handler in handlers.GetInvocationList())
                await handler(args);
        }

        private async Task NotifyMidiFileUnloadedAsync()
        {
            var handlers = MidiFileUnloaded;
            if (handlers is null)
                return;

            var args = new MidiEventArgs.FileUnloaded();

            foreach (Func<MidiEventArgs.FileUnloaded, Task> handler in handlers.GetInvocationList())
                await handler(args);
        }

        private async Task NotifyAssignmentsChangedAsync(PlaybackAssignmentChangeOperation operation)
        {
            var handlers = AssignmentsChanged;
            if (handlers is null)
                return;

            var args = new MidiEventArgs.AssignmentsChanged(
                Assignments.ToList(),
                ActivePans.ToList(),
                operation);

            foreach (Func<MidiEventArgs.AssignmentsChanged, Task> handler in handlers.GetInvocationList())
                await handler(args);
        }

        private async Task NotifyPanMixChangedAsync()
        {
            var handlers = PanMixChanged;
            if (handlers is null)
                return;

            var args = new MidiEventArgs.PanMixChanged(ActivePans.ToList());

            foreach (Func<MidiEventArgs.PanMixChanged, Task> handler in handlers.GetInvocationList())
                await handler(args);
        }

        private async Task NotifyClickTrackSettingsChangedAsync()
        {
            var handlers = ClickTrackSettingsChanged;
            if (handlers is null)
                return;

            var args = new MidiEventArgs.ClickTrackSettingsChanged(
                TempoBpm,
                BeatsPerBar,
                BeatUnit,
                ClickTrackEnabled);

            foreach (Func<MidiEventArgs.ClickTrackSettingsChanged, Task> handler in handlers.GetInvocationList())
                await handler(args);
        }


        private async Task PushPlaybackStateToJsAsync()
        {
            try
            {
                await js_.InvokeVoidAsync(
                    "panPlayback.setMidiPlaybackState",
                    new MidiEventArgs.PlaybackJsState(
                        IsPlaying,
                        Position.TotalSeconds,
                        Math.Max(Duration.TotalSeconds, 0.01),
                        MidiStartAt,
                        playbackAudioAnchorTime_,
                        Math.Max(InitialMidiBpm, 1),
                        Math.Max(TempoBpm, 1)));
            }
            catch (JSDisconnectedException)
            {
            }
        }

        private double GetEffectivePanVolume(MidiAssignedPan activePan)
        {
            var anyOtherSolo = !activePan.Soloing && ActivePans.Any(p => p.InstanceId != activePan.InstanceId && p.Soloing);
            return (activePan.Muted || anyOtherSolo) ? 0.0 : activePan.Volume;
        }

        private static SteelPan ClonePan(SteelPan source)
        {
            return new SteelPan
            {
                Type = source.Type,
                Notes = source.Notes
                    .Select(n => new PanNote
                    {
                        Note = n.Note,
                    })
                    .ToList(),
            };
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync(resetPosition: true);
            midiPlaybackCts_?.Dispose();
            playbackProgressCts_?.Dispose();

            navCallback_?.Dispose();
            state_.OnRefresh -= OnRefreshAsync;
        }
    }
}

public enum PlaybackAssignmentChangeOperation
{
    Add,
    Remove,
    Reload,
}

public static class MidiEventArgs
{
    public sealed record PlaybackStarted(
        double StartAt,
        TimeSpan StartOffset,
        IReadOnlyList<MidiPanEvent> PlaybackEvents);

    public sealed record PlaybackPaused(TimeSpan Position);

    public sealed record PlaybackStopped(bool ResetPosition, TimeSpan Position);

    public sealed record FileLoaded(
        Guid FileId,
        string FileName,
        IReadOnlyList<MidiTrackInfo> Tracks,
        int InitialBpm,
        int InitialBeatsPerBar,
        int InitialBeatUnit,
        bool Persisted);

    public sealed record FileUnloaded();

    public sealed record AssignmentsChanged(
        IReadOnlyList<MidiTrackAssignment> Assignments,
        IReadOnlyList<MidiAssignedPan> ActivePans,
        PlaybackAssignmentChangeOperation Operation);

    public sealed record PanMixChanged(IReadOnlyList<MidiAssignedPan> ActivePans);

    public sealed record ClickTrackSettingsChanged(
        int TempoBpm,
        int BeatsPerBar,
        int BeatUnit,
        bool Enabled);

    public sealed record PlaybackPositionChanged(
        TimeSpan Position,
        TimeSpan Duration,
        bool IsPlaying,
        bool IsTick);

    public sealed record PlaybackTempoChanged(int Bpm);

    public sealed record PlaybackCountInChanged(int Count, int NoteDivision);

    public sealed record PlaybackJsState(
        bool IsPlaying,
        double PositionSeconds,
        double DurationSeconds,
        double? MidiStartAt,
        double? AudioAnchorTime,
        int InitialMidiBpm,
        int TempoBpm);
}