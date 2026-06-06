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

    public class InstanceStateService : IDisposable
    {
        public readonly long MaxMidiFileSize = 64L * 1024L * 1024L;


        public TaskState Task { get; set; }
        public UserState User { get; set; }

        public InstanceStateService(DbService db, NavigationManager nav)
        {
            Task = new(nav);
            User = new(db, nav, Task);
        }

        public void Dispose()
        {
            Task.Dispose();
            User.Dispose();
        }


        public sealed class TaskState : IDisposable
        {
            public bool Busy { get; private set; } = false;
            public string Message { get; private set; } = string.Empty;
            public string Error { get; private set; } = string.Empty;

            private IDisposable navEvent_;

            public Task<bool> RunSafe(Func<Task<string>> job)
            {
                return Run(true, job);
            }
            public Task<bool> RunSafe(Func<Task> job)
            {
                return Run(true, job);
            }
            public Task<bool> RunUnsafe(Func<Task<string>> job)
            {
                return Run(false, job);
            }
            public Task<bool> RunUnsafe(Func<Task> job)
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

            private async Task<bool> Run(bool block, Func<Task<string>> job)
            {
                InitializeState(block);
                bool success = false;

                try
                {
                    Message = await job();
                    success = true;
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                    success = false;
                }
                finally
                {
                    Busy = false;
                }

                return success;
            }
            private async Task<bool> Run(bool block, Func<Task> job)
            {
                InitializeState(block);
                bool success = false;

                try
                {
                    await job();
                    success = true;
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                    success = false;
                }
                finally
                {
                    Busy = false;
                }

                return success;
            }
        }

        public sealed class UserState : IDisposable
        {
            public event Func<Task>? OnRefresh;

            public Guid Id { get; private set; } = Guid.Empty;
            public string CurrentLayout { get; set; } = string.Empty;
            public IReadOnlyList<GroupSummaryDto>? Groups { get; private set; }
            public Dictionary<Guid, IReadOnlyList<GroupFileDto>> GroupFiles { get; private set; } = [];
            public IReadOnlyList<GroupFileDto> Files { get; private set; } = [];

            private readonly DbService db_;
            private readonly NavigationManager nav_;
            private readonly TaskState task_;

            private readonly SemaphoreSlim refreshLock_ = new(1, 1);

            public UserState(DbService db, NavigationManager nav, TaskState task)
            {
                db_ = db;
                nav_ = nav;
                task_ = task;

                nav_.LocationChanged += OnNavigationAsync;
            }

            public void Dispose()
            {
                nav_.LocationChanged -= OnNavigationAsync;
                refreshLock_.Dispose();
            }

            public async ValueTask RefreshAsync()
            {
                await refreshLock_.WaitAsync();

                await task_.RunSafe(RunRefreshAsync);

                refreshLock_.Release();
            }

            private async Task RunRefreshAsync()
            {
                Id = db_.User;
                Groups = await db_.Groups.GetMyGroupsAsync();
                Files = await db_.MidiFiles.GetMyMidiFilesAsync();

                var groupFiles = new Dictionary<Guid, IReadOnlyList<GroupFileDto>>();

                foreach (var group in Groups)
                    groupFiles[group.Id] = await db_.Groups.GetGroupFilesAsync(group.Id);

                GroupFiles = groupFiles;

                await RaiseOnRefreshAsync();
            }

            private async void OnNavigationAsync(object? sender, LocationChangedEventArgs e)
            {
                await RefreshAsync();
            }

            private async Task RaiseOnRefreshAsync()
            {
                if (OnRefresh is null)
                    return;

                foreach (var handler in OnRefresh.GetInvocationList().Cast<Func<Task>>())
                    await handler();
            }
        }
    }
}
