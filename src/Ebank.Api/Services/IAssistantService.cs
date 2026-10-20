using Ebank.Api.Endpoints;

namespace Ebank.Api.Services;

public interface IAssistantService
{
    Task<AssistantChatResponse> RunAsync(
        IReadOnlyList<AssistantChatMessage> messages,
        string bearerToken,
        CancellationToken cancellationToken);
}

public sealed record AssistantMcpOptions(Uri Endpoint);
