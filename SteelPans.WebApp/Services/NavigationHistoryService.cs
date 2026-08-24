using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace SteelPans.WebApp.Services;

public sealed class NavigationHistoryService : IDisposable
{
    private readonly NavigationManager navigation_;
    private readonly Stack<string> history_ = new();
    private readonly Dictionary<string, Func<Task<bool>>> leaveGuards_ = new(StringComparer.Ordinal);
    private readonly IDisposable locationChangingRegistration_;

    private string currentUri_;
    private bool navigatingBack_;
    private string? pendingBackUri_;

    public NavigationHistoryService(NavigationManager navigation)
    {
        navigation_ = navigation;
        currentUri_ = NormalizeUri(navigation.Uri);

        locationChangingRegistration_ = navigation_.RegisterLocationChangingHandler(OnLocationChangingAsync);
        navigation_.LocationChanged += OnLocationChanged;
    }

    public bool CanGoBack => history_.Count > 0;

    public IDisposable RegisterLeaveGuard(Func<Task<bool>> canLeaveAsync)
    {
        ArgumentNullException.ThrowIfNull(canLeaveAsync);

        var uri = NormalizeUri(navigation_.Uri);
        leaveGuards_[uri] = canLeaveAsync;
        return new LeaveGuardRegistration(this, uri, canLeaveAsync);
    }

    public void Back(string fallbackUrl = "/")
    {
        var target = history_.TryPeek(out var previousUri)
            ? previousUri
            : navigation_.ToAbsoluteUri(fallbackUrl).ToString();

        pendingBackUri_ = NormalizeUri(target);
        navigation_.NavigateTo(target);
    }

    private async ValueTask OnLocationChangingAsync(LocationChangingContext context)
    {
        var targetUri = NormalizeUri(context.TargetLocation);
        if (string.Equals(currentUri_, targetUri, StringComparison.Ordinal))
            return;

        navigatingBack_ = string.Equals(targetUri, history_.FirstOrDefault(), StringComparison.Ordinal);
        if (!leaveGuards_.TryGetValue(currentUri_, out var canLeaveAsync))
            return;

        if (!await canLeaveAsync())
        {
            navigatingBack_ = false;
            pendingBackUri_ = null;
            context.PreventNavigation();
        }
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        var newUri = NormalizeUri(args.Location);
        if (string.Equals(currentUri_, newUri, StringComparison.Ordinal))
            return;

        if (navigatingBack_)
        {
            if (pendingBackUri_ is not null
                && string.Equals(newUri, pendingBackUri_, StringComparison.Ordinal)
                && history_.TryPeek(out var previousUri)
                && string.Equals(NormalizeUri(previousUri), pendingBackUri_, StringComparison.Ordinal))
            {
                history_.Pop();
            }
        }
        else
        {
            history_.Push(currentUri_);
        }

        pendingBackUri_ = null;
        currentUri_ = newUri;
    }

    private void UnregisterLeaveGuard(string uri, Func<Task<bool>> canLeaveAsync)
    {
        if (leaveGuards_.TryGetValue(uri, out var registered) && ReferenceEquals(registered, canLeaveAsync))
            leaveGuards_.Remove(uri);
    }

    private static string NormalizeUri(string uri)
    {
        var fragmentIndex = uri.IndexOf('#');
        return fragmentIndex >= 0 ? uri[..fragmentIndex] : uri;
    }

    public void Dispose()
    {
        locationChangingRegistration_.Dispose();
        navigation_.LocationChanged -= OnLocationChanged;
    }

    private sealed class LeaveGuardRegistration : IDisposable
    {
        private readonly NavigationHistoryService owner_;
        private readonly string uri_;
        private readonly Func<Task<bool>> canLeaveAsync_;
        private bool disposed_;

        public LeaveGuardRegistration(
            NavigationHistoryService owner,
            string uri,
            Func<Task<bool>> canLeaveAsync)
        {
            owner_ = owner;
            uri_ = uri;
            canLeaveAsync_ = canLeaveAsync;
        }

        public void Dispose()
        {
            if (disposed_)
                return;

            disposed_ = true;
            owner_.UnregisterLeaveGuard(uri_, canLeaveAsync_);
        }
    }
}
