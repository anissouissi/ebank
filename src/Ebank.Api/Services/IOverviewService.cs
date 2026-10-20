using Ebank.Api.Endpoints;

namespace Ebank.Api.Services;

public interface IOverviewService
{
    Task<OverviewSummaryResponse> GetAsync(
        string subject,
        CancellationToken cancellationToken);
}
