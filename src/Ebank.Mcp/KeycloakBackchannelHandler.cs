internal sealed class KeycloakBackchannelHandler(
    Uri publicIssuer,
    Uri backchannelAuthority) : DelegatingHandler(new HttpClientHandler())
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } requestUri &&
            string.Equals(
                requestUri.GetLeftPart(UriPartial.Authority),
                publicIssuer.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
        {
            request.RequestUri = new UriBuilder(requestUri)
            {
                Scheme = backchannelAuthority.Scheme,
                Host = backchannelAuthority.Host,
                Port = backchannelAuthority.Port
            }.Uri;
        }

        return base.SendAsync(request, cancellationToken);
    }
}
