using Parrot.SharedComponents.DesignSystem;
using Parrot.Web.Features.Auth.State;

namespace Parrot.Web.Features.Settings;

/// <summary>The workspace default for new entries. Saved records carry their own currency, independently.</summary>
public sealed record WorkspaceCurrencyValue(string Code, bool Ready = true, string? Problem = null)
{
    public static WorkspaceCurrencyValue Legacy { get; } = new(CurrencyAmount.LegacyCode);
    public string Symbol => Ready ? CurrencyAmount.Symbol(Code) : "";
}

/// <summary>Reads the existing business-details contract. No browser-global preference or currency conversion.</summary>
public sealed class WorkspaceCurrency(IBusinessDetailsStore store, AuthState auth)
{
    private string? _loadedKey;
    private int _revision;
    private Task? _loading;
    public WorkspaceCurrencyValue Current { get; private set; } = WorkspaceCurrencyValue.Legacy;
    public event Action? Changed;
    public string? WorkspaceKey => auth.CurrentUser is { } user && !string.IsNullOrWhiteSpace(user.CurrentWorkspaceId)
        ? $"{user.UserId}:{user.CurrentWorkspaceId}" : null;

    public Task EnsureAsync(bool retry = false)
    {
        var key = WorkspaceKey;
        if (key == _loadedKey && !retry) return _loading ?? Task.CompletedTask;
        _loadedKey = key;
        var revision = ++_revision;
        if (key is null || !store.Available)
        {
            Current = WorkspaceCurrencyValue.Legacy; _loading = null; Changed?.Invoke();
            return Task.CompletedTask;
        }
        Current = new(CurrencyAmount.LegacyCode, Ready: false); Changed?.Invoke();
        return _loading = ReadAsync(key, revision);
    }

    private async Task ReadAsync(string key, int revision)
    {
        try
        {
            var saved = await store.ReadAsync();
            if (_revision != revision || key != WorkspaceKey) return;
            Current = new(CurrencyAmount.Code(saved.Currency));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or NotSupportedException)
        {
            if (_revision != revision || key != WorkspaceKey) return;
            Current = new(CurrencyAmount.LegacyCode, Ready: false, Problem: "We couldn't load the business currency. Reload the page before adding an amount.");
        }
        if (_revision == revision) { _loading = null; Changed?.Invoke(); }
    }

    public void Saved(BusinessDetails details, string? workspaceKey)
    {
        if (workspaceKey is null || workspaceKey != WorkspaceKey) return;
        _revision++; _loadedKey = workspaceKey; _loading = null;
        Current = new(CurrencyAmount.Code(details.Currency)); Changed?.Invoke();
    }
}
