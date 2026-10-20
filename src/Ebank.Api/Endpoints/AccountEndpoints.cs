using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Ebank.Api.Data;
using Ebank.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ebank.Api.Endpoints;

public static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/accounts")
            .RequireAuthorization()
            .WithTags("Accounts");

        group.MapGet("/", async Task<Results<Ok<IReadOnlyList<AccountResponse>>, UnauthorizedHttpResult>> (
                ClaimsPrincipal user,
                IAccountService accountService,
                CancellationToken cancellationToken) =>
            {
                var subject = user.FindFirstValue("sub");
                if (string.IsNullOrWhiteSpace(subject))
                {
                    return TypedResults.Unauthorized();
                }

                return TypedResults.Ok(await accountService.GetAllAsync(subject, cancellationToken));
            })
            .WithName("GetCustomerAccounts")
            .WithSummary("List the signed-in customer's accounts")
            .WithDescription("Returns accounts belonging only to the authenticated Keycloak subject.")
            .Produces<IReadOnlyList<AccountResponse>>()
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("/{accountId:guid}", async Task<Results<Ok<AccountResponse>, NotFound, UnauthorizedHttpResult>> (
                Guid accountId,
                ClaimsPrincipal user,
                IAccountService accountService,
                CancellationToken cancellationToken) =>
            {
                var subject = user.FindFirstValue("sub");
                if (string.IsNullOrWhiteSpace(subject))
                {
                    return TypedResults.Unauthorized();
                }

                var account = await accountService.GetByIdAsync(
                    subject,
                    accountId,
                    cancellationToken);
                return account is null ? TypedResults.NotFound() : TypedResults.Ok(account);
            })
            .WithName("GetCustomerAccount")
            .WithSummary("Get one of the signed-in customer's accounts")
            .WithDescription("Returns an account only when it belongs to the authenticated customer.")
            .Produces<AccountResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/", async Task<Results<Created<AccountResponse>, UnauthorizedHttpResult, ValidationProblem>> (
                CreateAccountRequest request,
                ClaimsPrincipal user,
                IAccountService accountService,
                CancellationToken cancellationToken) =>
            {
                var subject = user.FindFirstValue("sub");
                if (string.IsNullOrWhiteSpace(subject))
                {
                    return TypedResults.Unauthorized();
                }

                if (!Enum.IsDefined(request.Type))
                {
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                    {
                        [nameof(request.Type)] = ["Choose Current or Savings."]
                    });
                }

                var account = await accountService.CreateAsync(
                    subject,
                    request.Type,
                    cancellationToken);
                return TypedResults.Created($"/api/accounts/{account.Id}", account);
            })
            .WithName("CreateCustomerAccount")
            .WithSummary("Open a current or savings demo account")
            .WithDescription("Creates a zero-balance demo account for the authenticated customer.")
            .Produces<AccountResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status401Unauthorized);

        return group;
    }
}

/// <summary>Specifies the type of customer account to open.</summary>
public sealed record CreateAccountRequest
{
    /// <summary>The account type to create: Current or Savings.</summary>
    [Required]
    public required AccountType Type { get; init; }
}

/// <summary>Represents a customer's demo account.</summary>
public sealed record AccountResponse(
    Guid Id,
    AccountType Type,
    string AccountNumber,
    string Currency,
    decimal Balance,
    DateTimeOffset CreatedAt);
