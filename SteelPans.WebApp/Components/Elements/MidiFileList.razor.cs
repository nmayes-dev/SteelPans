using Microsoft.AspNetCore.Components;
using Serilog.Core;
using SteelPans.Components.Services;
using SteelPans.Components.Toolbar;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Music;
using SteelPans.WebApp.Services;

namespace SteelPans.WebApp.Components.Elements;

public partial class MidiFileList
{
    [Parameter]
    public required IReadOnlyList<GroupFileDto> Files { get; set; }


    protected override void OnInitialized()
    {
        if (Files is null)
            throw new InvalidOperationException("Must provide file list");
    }

    private async Task LoadGroupMidiFileAsync(GroupFileDto file)
    {
        await Overlays.RequestCloseAllAsync();
        await MidiService.Playback.LoadGroupMidiFile(file.Id);
    }

}
