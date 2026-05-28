using System.Net.Http.Json;
using SteelPans.Shared.Ensembles;

namespace SteelPans.Shared.Services;

public sealed class EnsembleClient(HttpClient http)
{
    public async Task<IReadOnlyList<GroupSummaryDto>> GetMyGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        return await http.GetFromJsonAsync<List<GroupSummaryDto>>(
            "/api/groups/mine",
            cancellationToken) ?? [];
    }

    public async Task<GroupSummaryDto> CreateGroupAsync(
        CreateGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync(
            "/api/groups",
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<GroupSummaryDto>(
            cancellationToken) ?? throw new InvalidOperationException("No group returned.");
    }

    public async Task<IReadOnlyList<GroupFileDto>> GetGroupFilesAsync(
        Guid groupId,
        CancellationToken cancellationToken = default)
    {
        return await http.GetFromJsonAsync<List<GroupFileDto>>(
            $"/api/groups/{groupId}/files",
            cancellationToken) ?? [];
    }

    public async Task<MidiFileDetailsDto?> GetFileDetailsAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        return await http.GetFromJsonAsync<MidiFileDetailsDto>(
            $"/api/files/{fileId}",
            cancellationToken);
    }

    public async Task<Stream> DownloadFileAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync(
            $"/api/files/{fileId}/download",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    public async Task SaveAssignmentsAsync(
        Guid fileId,
        SaveMidiAssignmentsRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync(
            $"/api/files/{fileId}/assignments",
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }
}