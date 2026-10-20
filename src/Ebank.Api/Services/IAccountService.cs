using Ebank.Api.Data;
using Ebank.Api.Endpoints;

namespace Ebank.Api.Services;

public interface IAccountService
{
    Task<IReadOnlyList<AccountResponse>> GetAllAsync(
        string subject,
        CancellationToken cancellationToken);

    Task<AccountResponse?> GetByIdAsync(
        string subject,
        Guid accountId,
        CancellationToken cancellationToken);

    Task<AccountResponse> CreateAsync(
        string subject,
        AccountType type,
        CancellationToken cancellationToken);
}
