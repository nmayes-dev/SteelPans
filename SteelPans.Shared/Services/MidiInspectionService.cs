using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using SteelPans.Shared.Music;

namespace SteelPans.Shared.Services;

public sealed class MidiInspectionService
{
    public async Task<MidiFile> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        await using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        return MidiFile.Read(buffer);
    }

    public IReadOnlyList<MidiTrackInfo> GetTrackInfos(MidiFile midiFile)
    {
        return midiFile.GetTrackChunks()
            .Select((track, index) => new MidiTrackInfo
            {
                Index = index,
                Name = track.Events
                    .OfType<SequenceTrackNameEvent>()
                    .FirstOrDefault()?.Text,
                NoteCount = track.GetNotes().Count
            })
            .Where(x => x.NoteCount > 0)
            .ToList();
    }
}
