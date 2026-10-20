using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Ebank.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ebank.Api.Endpoints;

public static class AssistantEndpoints
{
    public static RouteGroupBuilder MapAssistantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/assistant")
            .RequireAuthorization()
            .WithTags("Assistant");

        group.MapPost("/chat", async Task<Results<
                Ok<AssistantChatResponse>,
                UnauthorizedHttpResult,
                ValidationProblem,
                StatusCodeHttpResult>> (
                AssistantChatRequest request,
                ClaimsPrincipal user,
                HttpContext httpContext,
                IAssistantService assistantService,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken) =>
            {
                var subject = user.FindFirstValue("sub");
                var authorization = httpContext.Request.Headers.Authorization.ToString();
                if (string.IsNullOrWhiteSpace(subject) ||
                    !System.Net.Http.Headers.AuthenticationHeaderValue.TryParse(
                        authorization,
                        out var bearer) ||
                    !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(bearer.Parameter))
                {
                    return TypedResults.Unauthorized();
                }

                var errors = ValidateRequest(request);
                if (errors.Count > 0)
                {
                    return TypedResults.ValidationProblem(errors);
                }

                try
                {
                    return TypedResults.Ok(await assistantService.RunAsync(
                        request.Messages,
                        bearer.Parameter,
                        cancellationToken));
                }
                catch (HttpRequestException exception)
                {
                    loggerFactory.CreateLogger("AssistantEndpoint").LogWarning(
                        "Assistant upstream request failed ({FailureType}, status {StatusCode}).",
                        exception.GetType().Name,
                        exception.StatusCode);
                    return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                catch (TimeoutException exception)
                {
                    loggerFactory.CreateLogger("AssistantEndpoint").LogWarning(
                        "Assistant provider timed out ({FailureType}).",
                        exception.GetType().Name);
                    return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                catch (OperationCanceledException exception)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    loggerFactory.CreateLogger("AssistantEndpoint").LogWarning(
                        "Assistant provider request timed out ({FailureType}).",
                        exception.GetType().Name);
                    return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            })
            .WithName("ChatWithAssistant")
            .WithSummary("Ask the authenticated customer's read-only Ebank assistant")
            .WithDescription(
                "Uses the signed-in customer's Keycloak access token to authorize read-only MCP account and transaction tools.")
            .Produces<AssistantChatResponse>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return group;
    }

    private static Dictionary<string, string[]> ValidateRequest(AssistantChatRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Messages is null || request.Messages.Count is < 1 or > 12)
        {
            errors[nameof(request.Messages)] = ["Provide between 1 and 12 recent chat messages."];
            return errors;
        }

        var totalLength = 0;
        for (var index = 0; index < request.Messages.Count; index++)
        {
            var message = request.Messages[index];
            if (message.Role is not ("user" or "assistant"))
            {
                errors[$"{nameof(request.Messages)}[{index}].{nameof(message.Role)}"] =
                    ["Role must be user or assistant."];
            }

            if (string.IsNullOrWhiteSpace(message.Content) || message.Content.Length > 4_000)
            {
                errors[$"{nameof(request.Messages)}[{index}].{nameof(message.Content)}"] =
                    ["Message content must be between 1 and 4,000 characters."];
            }

            totalLength += message.Content?.Length ?? 0;
        }

        if (request.Messages[^1].Role != "user")
        {
            errors[nameof(request.Messages)] = ["The latest message must be from the user."];
        }

        if (totalLength > 12_000)
        {
            errors[nameof(request.Messages)] = ["Conversation text must be 12,000 characters or fewer."];
        }

        return errors;
    }
}

/// <summary>Recent chat context submitted to the Ebank assistant.</summary>
public sealed record AssistantChatRequest
{
    /// <summary>One to twelve recent user and assistant messages, ending with a user message.</summary>
    [Required]
    public required IReadOnlyList<AssistantChatMessage> Messages { get; init; }
}

/// <summary>A single user or assistant conversation message.</summary>
public sealed record AssistantChatMessage
{
    /// <summary>The message role: user or assistant.</summary>
    [Required]
    public required string Role { get; init; }

    /// <summary>The message content.</summary>
    [Required, StringLength(4_000, MinimumLength = 1)]
    public required string Content { get; init; }
}

/// <summary>The assistant's response text.</summary>
public sealed record AssistantChatResponse(string Reply);
