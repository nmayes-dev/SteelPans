using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace SteelPans.WebApp.Services;

public sealed class NavigationHistoryService : IDisposable
{
    private readonly NavigationManager navigation_;
    private readonly Stack<string> history_ = new();
    private string currentUri_;
    private bool navigatingBack_;

    public NavigationHistoryService(NavigationManager navigation)
    {
        navigation_ = navigation;
        currentUri_ = navigation.Uri;
        navigation_.LocationChanged += OnLocationChanged;
    }

    public bool CanGoBack => history_.Count > 0;

    public void Back(string fallbackUrl = "/")
    {
        if (history_.TryPop(out var previousUri))
        {
            navigatingBack_ = true;
            navigation_.NavigateTo(previousUri);
            return;
        }

        navigatingBack_ = true;
        navigation_.NavigateTo(fallbackUrl);
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        if (string.Equals(currentUri_, args.Location, StringComparison.Ordinal))
            return;

        if (!navigatingBack_)
            history_.Push(currentUri_);

        navigatingBack_ = false;
        currentUri_ = args.Location;
    }

    public void Dispose()
    {
        navigation_.LocationChanged -= OnLocationChanged;
    }
}
