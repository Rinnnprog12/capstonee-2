namespace TsuOrg.Frontend.Services;

public sealed class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient(HttpClient http) => _http = http;

    public Task<HttpResponseMessage> HealthAsync(CancellationToken ct = default) =>
        _http.GetAsync("/health", ct);
}
