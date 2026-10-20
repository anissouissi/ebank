using System.Security.Claims;
using Ebank.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ebank.Api.Endpoints;

public static class OverviewEndpoints
{
    public static RouteGroupBuilder MapOverviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/overview")
            .RequireAuthorization()
            .WithTags("Overview");

        group.MapGet("/", async Task<Results<Ok<OverviewSummaryResponse>, UnauthorizedHttpResult>> (
                ClaimsPrincipal user,
                IOverviewService overviewService,
                CancellationToken cancellationToken) =>
            {
                var subject = user.FindFirstValue("sub");
                if (string.IsNullOrWhiteSpace(subject))
                {
                    return TypedResults.Unauthorized();
                }

                return TypedResults.Ok(await overviewService.GetAsync(subject, cancellationToken));
            })
            .WithName("GetCustomerOverview")
            .WithSummary("Get database-backed overview for the signed-in customer")
            .WithDescription(
                "Returns account balances, this month's recorded income and spending, and recent transactions for the current Keycloak subject.")
            .Produces<OverviewSummaryResponse>()
            .Produces(StatusCodes.Status401Unauthorized);

        return group;
    }
}

/// <summary>A balance total for a single currency.</summary>
public sealed record CurrencyTotalResponse(string Currency, decimal Amount);

/// <summary>Database-backed account and transaction information for the overview page.</summary>
public sealed record OverviewSummaryResponse(
    IReadOnlyList<CurrencyTotalResponse> Balances,
    IReadOnlyList<CurrencyTotalResponse> MonthlySpending,
    IReadOnlyList<CurrencyTotalResponse> MonthlyIncome,
    int AccountCount,
    int CurrentAccountCount,
    int SavingsAccountCount,
    IReadOnlyList<TransactionResponse> RecentTransactions);
