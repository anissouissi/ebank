using Ebank.Api.Data;
using Ebank.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Ebank.Api.Services;

public sealed class AccountService(EbankDbContext dbContext) : IAccountService
{
    public async Task<IReadOnlyList<AccountResponse>> GetAllAsync(
        string subject,
        CancellationToken cancellationToken)
    {
        var accounts = await dbContext.CustomerAccounts
            .AsNoTracking()
            .Where(account => account.Subject == subject)
            .OrderByDescending(account => account.CreatedAt)
            .ToListAsync(cancellationToken);
        return accounts.Select(ToResponse).ToArray();
    }

    public async Task<AccountResponse?> GetByIdAsync(
        string subject,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var account = await dbContext.CustomerAccounts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == accountId && candidate.Subject == subject,
                cancellationToken);
        return account is null ? null : ToResponse(account);
    }

    public async Task<AccountResponse> CreateAsync(
        string subject,
        AccountType type,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var account = new CustomerAccount
        {
            Id = id,
            Subject = subject,
            AccountNumber = $"EB-{id:N}",
            Type = type,
            Currency = "USD",
            Balance = 0m,
            CreatedAt = DateTimeOffset.UtcNow
        };

        dbContext.CustomerAccounts.Add(account);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(account);
    }

    private static AccountResponse ToResponse(CustomerAccount account) =>
        new(
            account.Id,
            account.Type,
            $"•••• {account.AccountNumber[^4..]}",
            account.Currency,
            account.Balance,
            account.CreatedAt);
}
