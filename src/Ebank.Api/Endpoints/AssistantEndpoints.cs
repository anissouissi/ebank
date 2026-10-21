using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
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
                ServerSentEventsResult<AssistantDebugEvent>,
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
                    if (request.DebugMode)
                    {
                        return TypedResults.ServerSentEvents(
                            StreamDebugEvents(
                                assistantService,
                                request.Messages,
                                bearer.Parameter,
                                loggerFactory.CreateLogger("AssistantEndpoint"),
                                cancellationToken),
                            "debug");
                    }

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
            .Produces<AssistantDebugEvent>(
                StatusCodes.Status200OK,
                contentType: "text/event-stream")
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return group;
    }

    private static async IAsyncEnumerable<AssistantDebugEvent> StreamDebugEvents(
        IAssistantService assistantService,
        IReadOnlyList<AssistantChatMessage> messages,
        string bearerToken,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = assistantService
            .RunDebugStreamAsync(messages, bearerToken, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            AssistantDebugEvent? current = null;
            Exception? failure = null;
            var hasNext = false;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
                if (hasNext)
                {
                    current = enumerator.Current;
                }
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(
                    "Assistant debug stream upstream request failed ({FailureType}, status {StatusCode}).",
                    exception.GetType().Name,
                    exception.StatusCode);
                failure = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogWarning(
                    "Assistant debug stream authorization failed ({FailureType}).",
                    exception.GetType().Name);
                failure = exception;
            }
            catch (InvalidOperationException exception)
            {
                logger.LogError(
                    exception,
                    "Assistant debug stream failed during agent or tool execution.");
                failure = exception;
            }
            catch (TimeoutException exception)
            {
                logger.LogWarning(
                    "Assistant debug stream timed out ({FailureType}).",
                    exception.GetType().Name);
                failure = exception;
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Assistant debug stream timed out ({FailureType}).",
                    exception.GetType().Name);
                failure = exception;
            }

            if (failure is not null)
            {
                yield return new AssistantDebugEvent(
                    "assistant",
                    "error",
                    "The assistant could not complete this request. Please try again.");
                yield break;
            }

            if (!hasNext)
            {
                yield break;
            }

            yield return current!;
        }
    }

    private static Dictionary<string, string[]> ValidateRequest(AssistantChatRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Messages is null || request.Messages.Count is < 1 or > 12)
        {
            errors[nameof(request.Messages)] = ["Provide between 1 and 12 recent chat messages."];
            return errors;
        }

        var totalLength = 0L;
        for (var index = 0; index < request.Messages.Count; index++)
        {
            var message = request.Messages[index];
            if (message.Role is not ("user" or "assistant"))
            {
                errors[$"{nameof(request.Messages)}[{index}].{nameof(message.Role)}"] =
                    ["Role must be user or assistant."];
            }

            var maximumLength = message.Role == "user" ? 4_000 : 256_000;
            if (string.IsNullOrWhiteSpace(message.Content) || message.Content.Length > maximumLength)
            {
                errors[$"{nameof(request.Messages)}[{index}].{nameof(message.Content)}"] =
                    [$"Message content must be between 1 and {maximumLength:N0} characters."];
            }

            totalLength += message.Content?.Length ?? 0;
        }

        if (request.Messages[^1].Role != "user")
        {
            errors[nameof(request.Messages)] = ["The latest message must be from the user."];
        }

        if (totalLength > 512_000)
        {
            errors[nameof(request.Messages)] = ["The complete request must contain no more than 512,000 characters."];
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

    /// <summary>Whether the response should include model reasoning and tool-call details.</summary>
    public bool DebugMode { get; init; }
}

/// <summary>A single user or assistant conversation message.</summary>
public sealed record AssistantChatMessage
{
    /// <summary>The message role: user or assistant.</summary>
    [Required]
    public required string Role { get; init; }

    /// <summary>The message content.</summary>
    [Required]
    public required string Content { get; init; }
}

/// <summary>The assistant's response text.</summary>
public sealed record AssistantChatResponse(string Reply);

/// <summary>A model, reasoning, or tool event included for debugging.</summary>
public sealed record AssistantDebugEvent(
    string Role,
    string Type,
    string Content);
