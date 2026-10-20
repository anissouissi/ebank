using Ebank.Api.Data;
using Ebank.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Ebank.Api.Services;

public sealed class OverviewService(EbankDbContext dbContext) : IOverviewService
{
    public async Task<OverviewSummaryResponse> GetAsync(
        string subject,
        CancellationToken cancellationToken)
    {
        var accounts = dbContext.CustomerAccounts
            .AsNoTracking()
            .Where(account => account.Subject == subject);

        var balances = await accounts
            .GroupBy(account => account.Currency)
            .Select(group => new CurrencyTotalResponse(group.Key, group.Sum(account => account.Balance)))
            .ToListAsync(cancellationToken);
        var accountCount = await accounts.CountAsync(cancellationToken);
        var currentAccountCount = await accounts
            .CountAsync(account => account.Type == AccountType.Current, cancellationToken);
        var savingsAccountCount = await accounts
            .CountAsync(account => account.Type == AccountType.Savings, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var nextMonth = monthStart.AddMonths(1);
        var monthTransactions = dbContext.AccountTransactions
            .AsNoTracking()
            .Where(transaction =>
                transaction.Account.Subject == subject &&
                transaction.OccurredAt >= monthStart &&
                transaction.OccurredAt < nextMonth);

        var monthlySpending = await monthTransactions
            .Where(transaction =>
                transaction.Amount < 0 &&
                transaction.Type != TransactionType.MoneyTransfer)
            .GroupBy(transaction => transaction.Currency)
            .Select(group => new CurrencyTotalResponse(
                group.Key,
                group.Sum(transaction => -transaction.Amount)))
            .ToListAsync(cancellationToken);
        var monthlyIncome = await monthTransactions
            .Where(transaction =>
                transaction.Amount > 0 &&
                transaction.Type != TransactionType.MoneyTransfer)
            .GroupBy(transaction => transaction.Currency)
            .Select(group => new CurrencyTotalResponse(
                group.Key,
                group.Sum(transaction => transaction.Amount)))
            .ToListAsync(cancellationToken);

        var recentTransactions = await dbContext.AccountTransactions
            .AsNoTracking()
            .Where(transaction => transaction.Account.Subject == subject)
            .Include(transaction => transaction.Account)
            .OrderByDescending(transaction => transaction.OccurredAt)
            .ThenByDescending(transaction => transaction.Id)
            .Take(5)
            .ToListAsync(cancellationToken);

        var recent = recentTransactions.Select(transaction => new TransactionResponse(
            transaction.Id,
            transaction.AccountId,
            transaction.Account.Type,
            $"•••• {transaction.Account.AccountNumber[^4..]}",
            transaction.Type,
            transaction.Label,
            transaction.Amount,
            transaction.Currency,
            transaction.OccurredAt)).ToArray();

        return new OverviewSummaryResponse(
            balances,
            monthlySpending,
            monthlyIncome,
            accountCount,
            currentAccountCount,
            savingsAccountCount,
            recent);
    }
}
