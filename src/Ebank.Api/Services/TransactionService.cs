using Ebank.Api.Data;
using Ebank.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Ebank.Api.Services;

public sealed class TransactionService(EbankDbContext dbContext) : ITransactionService
{
    private const decimal MaximumAccountBalance = 9_999_999_999_999_999.99m;

    public async Task<TransactionListResponse> GetAsync(
        string subject,
        Guid? accountId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.AccountTransactions
            .AsNoTracking()
            .Where(transaction => transaction.Account.Subject == subject);

        if (accountId.HasValue)
        {
            query = query.Where(transaction => transaction.AccountId == accountId.Value);
        }

        if (from.HasValue)
        {
            query = query.Where(transaction => transaction.OccurredAt >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(transaction => transaction.OccurredAt < to.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(transaction =>
                EF.Functions.ILike(transaction.Label, $"%{search}%"));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var transactions = await query
            .Include(transaction => transaction.Account)
            .OrderByDescending(transaction => transaction.OccurredAt)
            .ThenByDescending(transaction => transaction.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = transactions.Select(transaction => new TransactionResponse(
            transaction.Id,
            transaction.AccountId,
            transaction.Account.Type,
            $"•••• {transaction.Account.AccountNumber[^4..]}",
            transaction.Type,
            transaction.Label,
            transaction.Amount,
            transaction.Currency,
            transaction.OccurredAt)).ToArray();

        return new TransactionListResponse(items, page, pageSize, totalCount);
    }

    public async Task<TransactionResponse?> GetByIdAsync(
        string subject,
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        var transaction = await dbContext.AccountTransactions
            .AsNoTracking()
            .Where(item => item.Id == transactionId && item.Account.Subject == subject)
            .Include(item => item.Account)
            .SingleOrDefaultAsync(cancellationToken);

        return transaction is null ? null : ToResponse(transaction);
    }

    public async Task<DemoTransactionCreateResult> CreateDemoAsync(
        string subject,
        CreateDemoTransactionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Amount is <= 0 or > 1_000_000 ||
            decimal.Round(request.Amount, 2) != request.Amount ||
            request.Type is not (
                TransactionType.EcommercePurchase or
                TransactionType.AtmWithdrawal or
                TransactionType.SalaryReceived or
                TransactionType.MoneyTransfer) ||
            (request.Type == TransactionType.MoneyTransfer) != request.DestinationAccountId.HasValue ||
            request.DestinationAccountId == request.AccountId)
        {
            throw new ArgumentException("The demo transaction request is invalid.", nameof(request));
        }

        await using var dbTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var source = await dbContext.CustomerAccounts
            .SingleOrDefaultAsync(
                account => account.Id == request.AccountId && account.Subject == subject,
                cancellationToken);
        if (source is null)
        {
            return new DemoTransactionCreateResult(DemoTransactionStatus.AccountNotFound, null);
        }

        CustomerAccount? destination = null;
        if (request.DestinationAccountId is Guid destinationAccountId)
        {
            destination = await dbContext.CustomerAccounts
                .SingleOrDefaultAsync(
                    account => account.Id == destinationAccountId && account.Subject == subject,
                    cancellationToken);
            if (destination is null)
            {
                return new DemoTransactionCreateResult(DemoTransactionStatus.AccountNotFound, null);
            }

            if (!string.Equals(source.Currency, destination.Currency, StringComparison.Ordinal))
            {
                return new DemoTransactionCreateResult(DemoTransactionStatus.CurrencyMismatch, null);
            }
        }

        var isDebit = request.Type is TransactionType.EcommercePurchase
            or TransactionType.AtmWithdrawal
            or TransactionType.MoneyTransfer;
        if (isDebit)
        {
            var updated = await dbContext.CustomerAccounts
                .Where(account =>
                    account.Id == source.Id &&
                    account.Subject == subject &&
                    account.Balance >= request.Amount)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        account => account.Balance,
                        account => account.Balance - request.Amount),
                    cancellationToken);
            if (updated == 0)
            {
                return new DemoTransactionCreateResult(DemoTransactionStatus.InsufficientFunds, null);
            }
        }
        else
        {
            var updated = await dbContext.CustomerAccounts
                .Where(account =>
                    account.Id == source.Id &&
                    account.Subject == subject &&
                    account.Balance <= MaximumAccountBalance - request.Amount)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        account => account.Balance,
                        account => account.Balance + request.Amount),
                    cancellationToken);
            if (updated == 0)
            {
                return new DemoTransactionCreateResult(DemoTransactionStatus.BalanceLimitReached, null);
            }
        }

        if (destination is not null)
        {
            var updated = await dbContext.CustomerAccounts
                .Where(account =>
                    account.Id == destination.Id &&
                    account.Subject == subject &&
                    account.Balance <= MaximumAccountBalance - request.Amount)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        account => account.Balance,
                        account => account.Balance + request.Amount),
                    cancellationToken);
            if (updated == 0)
            {
                return new DemoTransactionCreateResult(DemoTransactionStatus.BalanceLimitReached, null);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var customLabel = request.Label?.Trim();
        var sourceLabel = customLabel ?? request.Type switch
        {
            TransactionType.EcommercePurchase => "E-commerce purchase",
            TransactionType.AtmWithdrawal => "ATM withdrawal",
            TransactionType.SalaryReceived => "Salary received",
            TransactionType.MoneyTransfer => $"Transfer to {MaskAccountNumber(destination!.AccountNumber)}",
            _ => throw new ArgumentException("Unsupported demo transaction type.", nameof(request))
        };
        var created = new List<AccountTransaction>
        {
            new()
            {
                Id = Guid.NewGuid(),
                AccountId = source.Id,
                Type = request.Type,
                Label = sourceLabel,
                Amount = isDebit ? -request.Amount : request.Amount,
                Currency = source.Currency,
                OccurredAt = now,
                Account = source
            }
        };

        if (destination is not null)
        {
            created.Add(new AccountTransaction
            {
                Id = Guid.NewGuid(),
                AccountId = destination.Id,
                Type = request.Type,
                Label = customLabel ?? $"Transfer from {MaskAccountNumber(source.AccountNumber)}",
                Amount = request.Amount,
                Currency = destination.Currency,
                OccurredAt = now,
                Account = destination
            });
        }

        dbContext.AccountTransactions.AddRange(created);
        await dbContext.SaveChangesAsync(cancellationToken);
        await dbTransaction.CommitAsync(cancellationToken);

        var response = new DemoTransactionResultResponse(
            request.Type,
            created.Select(ToResponse).ToArray());
        return new DemoTransactionCreateResult(DemoTransactionStatus.Created, response);
    }

    private static TransactionResponse ToResponse(AccountTransaction transaction) =>
        new(
            transaction.Id,
            transaction.AccountId,
            transaction.Account.Type,
            MaskAccountNumber(transaction.Account.AccountNumber),
            transaction.Type,
            transaction.Label,
            transaction.Amount,
            transaction.Currency,
            transaction.OccurredAt);

    private static string MaskAccountNumber(string accountNumber) =>
        $"•••• {accountNumber[^4..]}";
}
