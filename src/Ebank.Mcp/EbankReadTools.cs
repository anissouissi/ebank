using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Server;

[McpServerToolType]
internal sealed class EbankReadTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "list_accounts")]
    [Description("Lists the authenticated Ebank customer's accounts, including account IDs, types, balances, and currencies.")]
    public async Task<string> ListAccountsAsync(
        EbankApiClient ebankApi,
        CancellationToken cancellationToken)
    {
        var accounts = await ebankApi.GetAccountsAsync(cancellationToken);
        return accounts.GetRawText();
    }

    [McpServerTool(Name = "get_recent_transactions")]
    [Description("Gets the five newest transactions across all accounts owned by the authenticated Ebank customer.")]
    public Task<string> GetRecentTransactionsAsync(
        EbankApiClient ebankApi,
        CancellationToken cancellationToken) =>
        GetTransactionsJsonAsync(ebankApi, null, null, null, 5, cancellationToken);

    [McpServerTool(Name = "get_transactions_in_date_range")]
    [Description("Gets all transactions across the authenticated customer's accounts between inclusive UTC dates (yyyy-MM-dd). Maximum 1000 results; reports if more were found.")]
    public Task<string> GetTransactionsInDateRangeAsync(
        string fromDate,
        string toDate,
        EbankApiClient ebankApi,
        CancellationToken cancellationToken)
    {
        if (!TryParseDateRange(fromDate, toDate, out var from, out var to, out var error))
        {
            return Task.FromResult(JsonSerializer.Serialize(new { error }, JsonOptions));
        }

        return GetTransactionsJsonAsync(ebankApi, null, from, to, null, cancellationToken);
    }

    [McpServerTool(Name = "get_account_recent_transactions")]
    [Description("Gets the five newest transactions for the specified account ID, only if it belongs to the authenticated customer.")]
    public Task<string> GetAccountRecentTransactionsAsync(
        Guid accountId,
        EbankApiClient ebankApi,
        CancellationToken cancellationToken) =>
        GetTransactionsJsonAsync(ebankApi, accountId, null, null, 5, cancellationToken);

    [McpServerTool(Name = "get_account_transactions_in_date_range")]
    [Description("Gets transactions for the specified account ID between inclusive UTC dates (yyyy-MM-dd), only if the account belongs to the authenticated customer. Maximum 1000 results; reports if more were found.")]
    public Task<string> GetAccountTransactionsInDateRangeAsync(
        Guid accountId,
        string fromDate,
        string toDate,
        EbankApiClient ebankApi,
        CancellationToken cancellationToken)
    {
        if (!TryParseDateRange(fromDate, toDate, out var from, out var to, out var error))
        {
            return Task.FromResult(JsonSerializer.Serialize(new { error }, JsonOptions));
        }

        return GetTransactionsJsonAsync(ebankApi, accountId, from, to, null, cancellationToken);
    }

    private static async Task<string> GetTransactionsJsonAsync(
        EbankApiClient ebankApi,
        Guid? accountId,
        DateOnly? from,
        DateOnly? to,
        int? limit,
        CancellationToken cancellationToken)
    {
        var result = await ebankApi.GetTransactionsAsync(
            accountId,
            from,
            to,
            limit,
            cancellationToken);
        return JsonSerializer.Serialize(new
        {
            transactions = result.Items,
            result.TotalCount,
            result.Truncated
        }, JsonOptions);
    }

    private static bool TryParseDateRange(
        string fromDate,
        string toDate,
        out DateOnly from,
        out DateOnly to,
        out string? error)
    {
        var fromParsed = DateOnly.TryParseExact(
            fromDate,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out from);
        var toParsed = DateOnly.TryParseExact(
            toDate,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out to);
        if (!fromParsed || !toParsed)
        {
            error = "Dates must use yyyy-MM-dd format.";
            return false;
        }

        if (from > to || to.DayNumber - from.DayNumber > 366)
        {
            error = "The end date must be on or after the start date, and ranges cannot exceed 367 calendar days.";
            return false;
        }

        error = null;
        return true;
    }
}
