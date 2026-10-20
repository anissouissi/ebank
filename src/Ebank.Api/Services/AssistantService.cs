using Ebank.Api.Endpoints;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Ebank.Api.Services;

public sealed class AssistantService(
    IChatClient chatClient,
    AssistantMcpOptions mcpOptions,
    ILogger<AssistantService> logger) : IAssistantService
{
    private const string Instructions = """
        You are Ebank's read-only assistant for illustrative demo account data.
        Use the available MCP tools whenever a user asks about their accounts or transactions.
        Never invent account details, balances, dates, or transactions; report only tool results.
        Account IDs are internal identifiers. When the user names an account, use account data
        from list_accounts to resolve it. Ask a brief clarification if the account or date range
        is ambiguous. Dates in date-range tools are UTC calendar dates, inclusive at both ends.
        Explain when a result is truncated. You cannot make changes, move money, or perform
        banking operations. The access token is not part of your context and must never be requested.
        """;

    public async Task<AssistantChatResponse> RunAsync(
        IReadOnlyList<AssistantChatMessage> messages,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = mcpOptions.Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(15),
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {bearerToken}"
            }
        });

        await using var mcpClient = await McpClient.CreateAsync(
            transport,
            cancellationToken: cancellationToken);
        var tools = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
        var agent = chatClient.AsAIAgent(
            "EbankAssistant",
            Instructions,
            tools: tools.Cast<AITool>().ToList());
        var promptMessages = messages.Select(message => new ChatMessage(
            message.Role == "assistant" ? ChatRole.Assistant : ChatRole.User,
            message.Content));

        try
        {
            var response = await agent.RunAsync(
                promptMessages,
                cancellationToken: cancellationToken);
            return new AssistantChatResponse(response.Text ?? string.Empty);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                "Assistant provider request failed ({FailureType}, status {StatusCode}).",
                exception.GetType().Name,
                exception.StatusCode);
            throw;
        }
    }
}
