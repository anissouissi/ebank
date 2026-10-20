using Ebank.Api.Endpoints;

namespace Ebank.Api.Services;

public interface ITransactionService
{
    Task<TransactionListResponse> GetAsync(
        string subject,
        Guid? accountId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<TransactionResponse?> GetByIdAsync(
        string subject,
        Guid transactionId,
        CancellationToken cancellationToken);

    Task<DemoTransactionCreateResult> CreateDemoAsync(
        string subject,
        CreateDemoTransactionRequest request,
        CancellationToken cancellationToken);
}
