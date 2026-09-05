using System.Net.Http.Headers;

namespace TsuOrg.Frontend.Services;

public sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly ITokenStore _tokens;

    public BearerTokenHandler(ITokenStore tokens) => _tokens = tokens;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokens.GetAccessTokenAsync();
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken);
    }
}
