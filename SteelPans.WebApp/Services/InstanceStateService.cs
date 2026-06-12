using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using SteelPans.Shared.Data;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Music;
using SteelPans.Shared.Services;

namespace SteelPans.WebApp.Services;

public sealed class InstanceStateService : IAsyncDisposable
{
    public readonly long MaxMidiFileSize = 64L * 1024L * 1024L;

    public TaskState Task { get; }
    public UserState User { get; }

    public InstanceStateService(
        DbService db,
        NavigationManager nav,
        AppUpdatesService updates)
    {
        Task = new(nav);
        User = new(db, Task, updates);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.DisposeAsync();
        await User.DisposeAsync();
    }

    public sealed class TaskState : IAsyncDisposable
    {
        public bool Busy { get; private set; }
        public string Message { get; private set; } = string.Empty;
        public string Error { get; private set; } = string.Empty;

        private readonly IDisposable navEvent_;
        private bool disposed_;

        public TaskState(NavigationManager nav)
        {
            navEvent_ = nav.RegisterLocationChangingHandler(OnLocationChanged);
        }

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

        public ValueTask DisposeAsync()
        {
            if (disposed_)
                return ValueTask.CompletedTask;

            disposed_ = true;
            navEvent_.Dispose();

            return ValueTask.CompletedTask;
        }

        private ValueTask OnLocationChanged(LocationChangingContext context)
        {
            if (!Busy)
                InitializeState(block: false, resetMessage: true);

            return ValueTask.CompletedTask;
        }

        private void InitializeState(bool block, bool resetMessage)
        {
            Busy = block;
            Message = resetMessage ? string.Empty : Message;
            Error = string.Empty;
        }

        private async Task<bool> Run(bool block, Func<Task<string>> job)
        {
            InitializeState(block, resetMessage: true);

            try
            {
                Message = await job();
                return true;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                return false;
            }
            finally
            {
                Busy = false;
            }
        }

        private async Task<bool> Run(bool block, Func<Task> job)
        {
            InitializeState(block, resetMessage: false);

            try
            {
                await job();
                return true;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                return false;
            }
            finally
            {
                Busy = false;
            }
        }
    }

    public sealed class UserState : IAsyncDisposable
    {
        public event Func<Task>? OnRefresh;

        public Guid Id { get; private set; } = Guid.Empty;
        public string CurrentLayout { get; set; } = string.Empty;
        public IReadOnlyList<GroupSummaryDto>? Groups { get; private set; }
        public Dictionary<Guid, IReadOnlyList<GroupFileDto>> GroupFiles { get; private set; } = [];
        public IReadOnlyList<GroupFileDto> Files { get; private set; } = [];

        private readonly DbService db_;
        private readonly TaskState task_;
        private readonly AppUpdatesService updates_;

        private readonly SemaphoreSlim refreshLock_ = new(1, 1);

        private bool disposed_;
        private bool refreshPending_;

        public UserState(
            DbService db,
            TaskState task,
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

            Groups = await db_.Groups.GetMyGroupsAsync();
            Files = await db_.MidiFiles.GetMyMidiFilesAsync();

            var groupFiles = new Dictionary<Guid, IReadOnlyList<GroupFileDto>>();

            foreach (var group in Groups)
                groupFiles[group.Id] = await db_.Groups.GetGroupFilesAsync(group.Id);

            GroupFiles = groupFiles;

            try
            {
                await updates_.StartAsync(
                    Id,
                    Groups.Select(x => x.Id));
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"SignalR update client failed to start: {ex.Message}");
            }

            await RaiseOnRefreshAsync();
        }

        private Task OnRealtimeUserStateChangedAsync()
        {
            return RefreshAsync().AsTask();
        }

        private Task OnRealtimeGroupChangedAsync(Guid groupId)
        {
            if (Groups?.Any(x => x.Id == groupId) != true)
                return System.Threading.Tasks.Task.CompletedTask;

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
}