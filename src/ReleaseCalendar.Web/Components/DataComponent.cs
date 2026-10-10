using Microsoft.AspNetCore.Components;
using ReleaseCalendar.Web.Services;

namespace ReleaseCalendar.Web.Components;

/// <summary>
/// A page whose data comes from the cache and should reload itself whenever a sync lands —
/// so an undated game appearing with a real date shows up without a manual refresh.
/// </summary>
public abstract class DataComponent : ComponentBase, IDisposable
{
    [Inject] protected SyncService Sync { get; set; } = default!;

    private bool _subscribed;

    protected override async Task OnInitializedAsync()
    {
        Sync.SyncCompleted += OnSyncCompletedAsync;
        _subscribed = true;
        await LoadAsync();
    }

    protected abstract Task LoadAsync();

    private async Task OnSyncCompletedAsync()
    {
        await LoadAsync();
        await InvokeAsync(StateHasChanged);
    }

    protected async Task SyncNowAsync(bool force = false)
    {
        await Sync.SyncAsync(force);
    }

    public void Dispose()
    {
        if (_subscribed) Sync.SyncCompleted -= OnSyncCompletedAsync;
        GC.SuppressFinalize(this);
    }
}
