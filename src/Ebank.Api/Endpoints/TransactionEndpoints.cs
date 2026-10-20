using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Ebank.Api.Data;
using Ebank.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Ebank.Api.Endpoints;

public static class TransactionEndpoints
{
    public static RouteGroupBuilder MapTransactionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/transactions")
            .RequireAuthorization()
            .WithTags("Transactions");

        group.MapGet("/{transactionId:guid}", async Task<Results<Ok<TransactionResponse>, NotFound, UnauthorizedHttpResult>> (
                Guid transactionId,
                ClaimsPrincipal user,
                ITransactionService transactionService,
                CancellationToken cancellationToken) =>
            {
                var subject = user.FindFirstValue("sub");
                if (string.IsNullOrWhiteSpace(subject))
                {
                    return TypedResults.Unauthorized();
                }

                var transaction = await transactionService.GetByIdAsync(
                    subject,
                    transactionId,
                    cancellationToken);
                return transaction is null ? TypedResults.NotFound() : TypedResults.Ok(transaction);
            })
            .WithName("GetCustomerTransaction")
            .WithSummary("Get a transaction owned by the signed-in customer")
            .Produces<TransactionResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("/", async Task<Results<Ok<TransactionListResponse>, UnauthorizedHttpResult, ValidationProblem>> (
                ClaimsPrincipal user,
                ITransactionService transactionService,
                Guid? accountId,
                DateTimeOffset? from,
                DateTimeOffset? to,
                string? search,
                int page = 1,
                int pageSize = 25,
                CancellationToken cancellationToken = default) =>
            {
                var subject = user.FindFirstValue("sub");
                if (string.IsNullOrWhiteSpace(subject))
                {
                    return TypedResults.Unauthorized();
                }

                var errors = new Dictionary<string, string[]>();
                if (from.HasValue && to.HasValue && from >= to)
                {
                    errors["to"] = ["The end date must be later than the start date."];
                }

                if (search?.Length > 100)
                {
                    errors["search"] = ["Search text must be 100 characters or fewer."];
                }

                if (page is < 1 or > 1_000_000)
                {
                    errors["page"] = ["Page must be between 1 and 1,000,000."];
                }

                if (pageSize is < 1 or > 100)
                {
                    errors["pageSize"] = ["Page size must be between 1 and 100."];
                }

                if (errors.Count > 0)
                {
                    return TypedResults.ValidationProblem(errors);
                }

                var result = await transactionService.GetAsync(
                    subject,
                    accountId,
                    from,
                    to,
                    search?.Trim(),
                    page,
                    pageSize,
                    cancellationToken);
                return TypedResults.Ok(result);
            })
            .WithName("GetCustomerTransactions")
            .WithSummary("List the signed-in customer's transactions")
            .WithDescription(
                "Returns the customer's transactions, newest first, optionally filtered by account, time range, or transaction label.")
            .Produces<TransactionListResponse>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/demo", async Task<Results<
                Created<DemoTransactionResultResponse>,
                UnauthorizedHttpResult,
                NotFound,
                Conflict<ProblemDetails>,
                ValidationProblem>> (
                CreateDemoTransactionRequest request,
                ClaimsPrincipal user,
                ITransactionService transactionService,
                CancellationToken cancellationToken) =>
            {
                var subject = user.FindFirstValue("sub");
                if (string.IsNullOrWhiteSpace(subject))
                {
                    return TypedResults.Unauthorized();
                }

                var errors = new Dictionary<string, string[]>();
                if (request.Type is not (
                    TransactionType.EcommercePurchase or
                    TransactionType.AtmWithdrawal or
                    TransactionType.SalaryReceived or
                    TransactionType.MoneyTransfer))
                {
                    errors[nameof(request.Type)] = ["Choose a supported demo transaction type."];
                }

                if (request.Amount is <= 0 or > 1_000_000 ||
                    decimal.Round(request.Amount, 2) != request.Amount)
                {
                    errors[nameof(request.Amount)] =
                        ["Amount must be a positive value with at most two decimal places, no more than 1,000,000."];
                }

                if (request.Label?.Length > 160)
                {
                    errors[nameof(request.Label)] = ["Label must be 160 characters or fewer."];
                }

                if (request.Type == TransactionType.MoneyTransfer &&
                    request.DestinationAccountId is null)
                {
                    errors[nameof(request.DestinationAccountId)] =
                        ["Select a destination account for the transfer."];
                }

                if (request.Type != TransactionType.MoneyTransfer &&
                    request.DestinationAccountId is not null)
                {
                    errors[nameof(request.DestinationAccountId)] =
                        ["A destination account is only used for transfers."];
                }

                if (request.DestinationAccountId == request.AccountId)
                {
                    errors[nameof(request.DestinationAccountId)] =
                        ["Choose a different destination account."];
                }

                if (errors.Count > 0)
                {
                    return TypedResults.ValidationProblem(errors);
                }

                var result = await transactionService.CreateDemoAsync(
                    subject,
                    request,
                    cancellationToken);

                return result.Status switch
                {
                    DemoTransactionStatus.Created =>
                        TypedResults.Created(
                            $"/api/transactions/{result.Response!.Transactions[0].Id}",
                            result.Response),
                    DemoTransactionStatus.AccountNotFound => TypedResults.NotFound(),
                    DemoTransactionStatus.InsufficientFunds =>
                        TypedResults.Conflict(new ProblemDetails
                        {
                            Status = StatusCodes.Status409Conflict,
                            Title = "Insufficient funds",
                            Detail = "The source account does not have enough balance for this demo transaction."
                        }),
                    DemoTransactionStatus.CurrencyMismatch =>
                        TypedResults.Conflict(new ProblemDetails
                        {
                            Status = StatusCodes.Status409Conflict,
                            Title = "Currency mismatch",
                            Detail = "A demo transfer requires source and destination accounts to use the same currency."
                        }),
                    DemoTransactionStatus.BalanceLimitReached =>
                        TypedResults.Conflict(new ProblemDetails
                        {
                            Status = StatusCodes.Status409Conflict,
                            Title = "Account balance limit reached",
                            Detail = "The resulting account balance exceeds the supported demo account limit."
                        }),
                    _ => throw new InvalidOperationException("Unsupported demo transaction result.")
                };
            })
            .WithName("CreateDemoTransaction")
            .WithSummary("Create a demo transaction on the signed-in customer's account")
            .WithDescription(
                "Records illustrative spending, ATM withdrawal, salary, or transfer activity and updates demo account balances. Transfers create matching debit and credit entries.")
            .Produces<DemoTransactionResultResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        return group;
    }
}

/// <summary>Request to create illustrative activity on the customer's demo accounts.</summary>
public sealed record CreateDemoTransactionRequest
{
    /// <summary>The account to charge or credit.</summary>
    [Required]
    public required Guid AccountId { get; init; }

    /// <summary>The demo activity type.</summary>
    [Required]
    public required TransactionType Type { get; init; }

    /// <summary>A positive amount in the account's currency.</summary>
    [Range(typeof(decimal), "0.01", "1000000")]
    public required decimal Amount { get; init; }

    /// <summary>Optional destination account; required for money transfers.</summary>
    public Guid? DestinationAccountId { get; init; }

    /// <summary>Optional display label; a type-specific label is used when omitted.</summary>
    [StringLength(160)]
    public string? Label { get; init; }
}

/// <summary>The result of creating a demo transaction, including paired transfer entries.</summary>
public sealed record DemoTransactionResultResponse(
    TransactionType Type,
    IReadOnlyList<TransactionResponse> Transactions);

public enum DemoTransactionStatus
{
    Created,
    AccountNotFound,
    InsufficientFunds,
    CurrencyMismatch,
    BalanceLimitReached
}

public sealed record DemoTransactionCreateResult(
    DemoTransactionStatus Status,
    DemoTransactionResultResponse? Response);

/// <summary>A transaction belonging to one of the customer's accounts.</summary>
public sealed record TransactionResponse(
    Guid Id,
    Guid AccountId,
    AccountType AccountType,
    string AccountNumber,
    TransactionType Type,
    string Label,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAt);

/// <summary>A page of transactions matching the customer's filters.</summary>
public sealed record TransactionListResponse(
    IReadOnlyList<TransactionResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);
