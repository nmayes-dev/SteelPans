using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using SteelPans.Shared.Config;
using SteelPans.Shared.Data;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Music;
using SteelPans.Shared.Services;
using System.Threading.Channels;

namespace SteelPans.WebApp.Services
{

    public class InstanceStateService(DbService db, NavigationManager nav)
    {
        public readonly long MaxMidiFileSize = 64L * 1024L * 1024L;


        public TaskState Task { get; set; } = new(nav);
        public UserState User { get; set; } = new(db);


        public sealed class TaskState : IDisposable
        {
            public bool Busy { get; private set; } = false;
            public string Message { get; private set; } = string.Empty;
            public string Error { get; private set; } = string.Empty;

            private IDisposable navEvent_;

            public Task RunSafe(Func<Task<string>> job)
            {
                return Run(true, job);
            }
            public Task RunSafe(Func<Task> job)
            {
                return Run(true, job);
            }
            public Task RunUnsafe(Func<Task<string>> job)
            {
                return Run(false, job);
            }
            public Task RunUnsafe(Func<Task> job)
            {
                return Run(false, job);
            }

            public TaskState(NavigationManager nav)
            {
                navEvent_ = nav.RegisterLocationChangingHandler(OnLocationChanged);
            }

            public void Dispose()
            {
                navEvent_.Dispose();
            }

            private async ValueTask OnLocationChanged(LocationChangingContext context)
            {
                if (Busy)
                    return;

                InitializeState(false);
            }
            private void InitializeState(bool block)
            {
                Busy = block;
                Message = string.Empty;
                Error = string.Empty;
            }

            private async Task Run(bool block, Func<Task<string>> job)
            {
                InitializeState(block);

                try
                {
                    Message = await job();
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                }
                finally
                {
                    Busy = false;
                }
            }
            private async Task Run(bool block, Func<Task> job)
            {
                InitializeState(block);

                try
                {
                    await job();
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                }
                finally
                {
                    Busy = false;
                }
            }
        }

        public sealed class UserState(DbService db)
        {
            public event Func<Task>? OnRefresh;

            public string CurrentLayout { get; set; } = string.Empty;
            public IReadOnlyList<GroupSummaryDto>? Groups { get; set; }
            public Dictionary<Guid, IReadOnlyList<GroupFileDto>> GroupFiles { get; set; } = [];
            public IReadOnlyList<GroupFileDto> Files { get; set; } = [];

            public async Task RefreshAsync()
            {
                Groups = await db.Groups.GetMyGroupsAsync();
                Files = await db.MidiFiles.GetMyMidiFilesAsync();

                GroupFiles.Clear();

                foreach (var group in Groups)
                    GroupFiles[group.Id] = await db.Groups.GetGroupFilesAsync(group.Id);

                RaiseOnRefresh();
            }

            private void RaiseOnRefresh()
            {
                if (OnRefresh is null)
                    return;

                foreach (var handler in OnRefresh.GetInvocationList().Cast<Func<Task>>())
                    _ = System.Threading.Tasks.Task.Run(handler);
            }
        }
    }
}
