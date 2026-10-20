using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

internal sealed class EbankApiClient(HttpClient httpClient, IHttpContextAccessor httpContextAccessor)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<JsonElement> GetAccountsAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/accounts");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken);
    }

    public async Task<TransactionQueryResult> GetTransactionsAsync(
        Guid? accountId,
        DateOnly? fromDate,
        DateOnly? toDate,
        int? limit,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? from = fromDate is null
            ? null
            : new DateTimeOffset(fromDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        DateTimeOffset? to = toDate is null
            ? null
            : new DateTimeOffset(toDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        const int maximumResults = 1000;
        var resultLimit = Math.Min(maximumResults, limit ?? maximumResults);
        var pageSize = limit.HasValue ? Math.Min(limit.Value, 100) : 100;
        var page = 1;
        var totalCount = 0;
        var items = new List<JsonElement>();

        while (items.Count < resultLimit)
        {
            var query = new List<string>
            {
                $"page={page}",
                $"pageSize={pageSize}"
            };
            if (accountId.HasValue)
            {
                query.Add($"accountId={accountId.Value}");
            }

            if (from.HasValue)
            {
                query.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
            }

            if (to.HasValue)
            {
                query.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
            }

            using var request = CreateRequest(
                HttpMethod.Get,
                $"/api/transactions?{string.Join('&', query)}");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);

            var result = await response.Content.ReadFromJsonAsync<TransactionListResponse>(
                JsonOptions,
                cancellationToken)
                ?? throw new InvalidOperationException("The Ebank API returned an empty transaction response.");
            totalCount = result.TotalCount;
            items.AddRange(result.Items);
            if (items.Count >= totalCount || result.Items.Count < pageSize)
            {
                break;
            }

            page++;
        }

        return new TransactionQueryResult(
            items.Take(resultLimit).ToArray(),
            totalCount,
            limit is null && totalCount > maximumResults);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var context = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("The MCP request context is unavailable.");
        if (context.User.Identity?.IsAuthenticated != true)
        {
            throw new UnauthorizedAccessException("An authenticated Ebank user is required.");
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        if (!AuthenticationHeaderValue.TryParse(authorization, out var bearer) ||
            !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(bearer.Parameter))
        {
            throw new UnauthorizedAccessException("A valid bearer token is required.");
        }

        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = bearer;
        return request;
    }

    private static Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (response.IsSuccessStatusCode)
        {
            return Task.CompletedTask;
        }

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException(
                $"The Ebank API rejected the authenticated MCP request ({(int)response.StatusCode}).");
        }

        var statusCode = (int)response.StatusCode;
        throw new HttpRequestException(
            $"The Ebank API returned HTTP {statusCode}.",
            null,
            response.StatusCode);
    }
}

internal sealed record TransactionListResponse(
    IReadOnlyList<JsonElement> Items,
    int Page,
    int PageSize,
    int TotalCount);

internal sealed record TransactionQueryResult(
    IReadOnlyList<JsonElement> Items,
    int TotalCount,
    bool Truncated);
