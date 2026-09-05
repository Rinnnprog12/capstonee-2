using Microsoft.JSInterop;

namespace TsuOrg.Frontend.Services;

public sealed class LocalStorageTokenStore : ITokenStore
{
    private const string AccessKey = "tsuorg.accessToken";
    private const string RefreshKey = "tsuorg.refreshToken";

    private readonly IJSRuntime _js;

    public LocalStorageTokenStore(IJSRuntime js) => _js = js;

    public async Task<string?> GetAccessTokenAsync() =>
        await _js.InvokeAsync<string?>("localStorage.getItem", AccessKey);

    public async Task<string?> GetRefreshTokenAsync() =>
        await _js.InvokeAsync<string?>("localStorage.getItem", RefreshKey);

    public async Task SaveAsync(string accessToken, string refreshToken)
    {
        await _js.InvokeVoidAsync("localStorage.setItem", AccessKey, accessToken);
        await _js.InvokeVoidAsync("localStorage.setItem", RefreshKey, refreshToken);
    }

    public async Task ClearAsync()
    {
        await _js.InvokeVoidAsync("localStorage.removeItem", AccessKey);
        await _js.InvokeVoidAsync("localStorage.removeItem", RefreshKey);
    }
}
