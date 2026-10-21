using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Ebank.Api.Endpoints;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Ebank.Api.Services;

public sealed class AssistantService(
    IChatClient chatClient,
    AssistantMcpOptions mcpOptions,
    ILogger<AssistantService> logger) : IAssistantService
{
    private const int MaximumPromptCharacters = 12_000;
    private const int MaximumPromptMessages = 8;
    private const int MaximumAssistantHistoryMessageLength = 1_000;
    private const int MaximumOutputTokens = 4_096;
    private const string LongAssistantHistorySummary =
        "A previous assistant reply was omitted; retrieve any needed account or transaction details again using the available tools.";

    private const string Instructions = """
        You are Ebank's read-only assistant for illustrative demo account data.
        Select and invoke the appropriate available MCP tool for every account or transaction
        question, then answer using its result. Use list_accounts for account and balance questions;
        get_recent_transactions for the five newest transactions across all accounts;
        get_account_recent_transactions for the five newest transactions on one account;
        get_transactions_in_date_range for a date range across all accounts; and
        get_account_transactions_in_date_range for an account and date range. Pass a UUID the user
        identifies as an account ID as the accountId tool argument. The tools scope results to the
        authenticated customer; never assume ownership or invent results.
        Date-range tool dates use yyyy-MM-dd UTC calendar dates, inclusive at both ends.
        Ask a concise clarification if the account or date range is ambiguous.
        Explain when a result is truncated. You cannot make changes, move money, or perform
        banking operations. Return only a concise customer-facing answer based on tool results;
        do not describe tool selection or your process. The access token is not part of your
        context and must never be requested.
        """;

    public async Task<AssistantChatResponse> RunAsync(
        IReadOnlyList<AssistantChatMessage> messages,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        var requestStartedAt = Stopwatch.GetTimestamp();
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
        var mcpConnectedAt = Stopwatch.GetTimestamp();
        var tools = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
        var toolsListedAt = Stopwatch.GetTimestamp();
        var agent = CreateAgent(tools.Cast<AITool>().ToList());
        var promptMessages = PreparePromptMessages(messages);

        try
        {
            var response = await agent.RunAsync(
                promptMessages,
                options: CreateRunOptions(debugMode: false),
                cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(response.Text))
            {
                logger.LogWarning(
                    "Assistant completed without user-facing text after {ElapsedMilliseconds} ms.",
                    Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds);
                throw new HttpRequestException(
                    "The assistant did not produce a user-facing response.",
                    null,
                    System.Net.HttpStatusCode.ServiceUnavailable);
            }

            logger.LogInformation(
                "Assistant request completed. MCP connect {McpConnectMilliseconds} ms, " +
                "tool discovery {ToolDiscoveryMilliseconds} ms, model and tool run {AgentRunMilliseconds} ms, " +
                "total {TotalMilliseconds} ms, reply length {ReplyLength}.",
                Stopwatch.GetElapsedTime(requestStartedAt, mcpConnectedAt).TotalMilliseconds,
                Stopwatch.GetElapsedTime(mcpConnectedAt, toolsListedAt).TotalMilliseconds,
                Stopwatch.GetElapsedTime(toolsListedAt).TotalMilliseconds,
                Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds,
                response.Text.Length);
            return new AssistantChatResponse(response.Text);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                "Assistant provider request failed after {ElapsedMilliseconds} ms " +
                "({FailureType}, status {StatusCode}).",
                Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds,
                exception.GetType().Name,
                exception.StatusCode);
            throw;
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Assistant provider request timed out after {ElapsedMilliseconds} ms ({FailureType}).",
                Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds,
                exception.GetType().Name);
            throw;
        }
    }

    public async IAsyncEnumerable<AssistantDebugEvent> RunDebugStreamAsync(
        IReadOnlyList<AssistantChatMessage> messages,
        string bearerToken,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var requestStartedAt = Stopwatch.GetTimestamp();
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
        var mcpConnectedAt = Stopwatch.GetTimestamp();
        var tools = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
        var toolsListedAt = Stopwatch.GetTimestamp();
        var agent = CreateAgent(tools.Cast<AITool>().ToList());
        var reply = new StringBuilder();
        long? lastOutputTokenCount = null;
        var maximumOutputTokens = MaximumOutputTokens;

        await foreach (var update in agent.RunStreamingAsync(
                PreparePromptMessages(messages),
                options: CreateRunOptions(debugMode: true),
                cancellationToken: cancellationToken)
            .WithCancellation(cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent reasoning:
                        yield return new AssistantDebugEvent(
                            update.Role?.ToString() ?? "assistant",
                            "reasoning",
                            reasoning.Text);
                        break;
                    case FunctionCallContent functionCall:
                        yield return new AssistantDebugEvent(
                            update.Role?.ToString() ?? "assistant",
                            "tool_call",
                            $"{functionCall.Name}\n{SerializeDebugValue(functionCall.Arguments)}");
                        break;
                    case FunctionResultContent functionResult:
                        yield return new AssistantDebugEvent(
                            update.Role?.ToString() ?? "tool",
                            "tool_result",
                            SerializeDebugValue(functionResult.Result));
                        break;
                    case TextContent text:
                        reply.Append(text.Text);
                        yield return new AssistantDebugEvent(
                            update.Role?.ToString() ?? "assistant",
                            "answer_delta",
                            text.Text);
                        break;
                    case UsageContent usage:
                        lastOutputTokenCount = usage.Details.OutputTokenCount;
                        yield return new AssistantDebugEvent(
                            update.Role?.ToString() ?? "assistant",
                            "usage",
                            SerializeDebugValue(new
                            {
                                inputTokens = usage.Details.InputTokenCount,
                                outputTokens = usage.Details.OutputTokenCount,
                                reasoningTokens = usage.Details.ReasoningTokenCount,
                                totalTokens = usage.Details.TotalTokenCount
                            }));
                        break;
                    default:
                        yield return new AssistantDebugEvent(
                            update.Role?.ToString() ?? "assistant",
                            content.GetType().Name,
                            content.ToString() ?? string.Empty);
                        break;
                }
            }
        }

        logger.LogInformation(
            "Assistant debug stream completed. MCP connect {McpConnectMilliseconds} ms, " +
            "tool discovery {ToolDiscoveryMilliseconds} ms, model and tool run {AgentRunMilliseconds} ms, " +
            "total {TotalMilliseconds} ms, reply length {ReplyLength}, output tokens {OutputTokens}.",
            Stopwatch.GetElapsedTime(requestStartedAt, mcpConnectedAt).TotalMilliseconds,
            Stopwatch.GetElapsedTime(mcpConnectedAt, toolsListedAt).TotalMilliseconds,
            Stopwatch.GetElapsedTime(toolsListedAt).TotalMilliseconds,
            Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds,
            reply.Length,
            lastOutputTokenCount);

        if (reply.Length == 0)
        {
            var error = lastOutputTokenCount >= maximumOutputTokens
                ? "The model reached the debug output limit before returning an answer. Try a shorter request or turn off debug mode."
                : "The assistant did not produce a user-facing response.";
            logger.LogWarning(
                "Assistant debug stream produced no answer. Output tokens {OutputTokens}, configured limit {OutputTokenLimit}.",
                lastOutputTokenCount,
                maximumOutputTokens);
            yield return new AssistantDebugEvent(
                "assistant",
                "error",
                error);
            yield break;
        }

        yield return new AssistantDebugEvent("assistant", "complete", string.Empty);
    }

    private AIAgent CreateAgent(IList<AITool> tools) =>
        chatClient.AsAIAgent("EbankAssistant", Instructions, tools: tools);

    private static ChatClientAgentRunOptions CreateRunOptions(bool debugMode)
    {
        return new ChatClientAgentRunOptions(new ChatOptions
        {
            MaxOutputTokens = MaximumOutputTokens,
            Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.Low,
                Output = debugMode ? ReasoningOutput.Full : ReasoningOutput.None
            }
        });
    }

    private static string SerializeDebugValue(object? value) =>
        value is string text
            ? text
            : JsonSerializer.Serialize(value);

    private static IReadOnlyList<ChatMessage> PreparePromptMessages(
        IReadOnlyList<AssistantChatMessage> messages)
    {
        var prepared = new List<ChatMessage>();
        var totalLength = 0;
        foreach (var message in messages.TakeLast(MaximumPromptMessages).Reverse())
        {
            var content = message.Role == "assistant" &&
                message.Content.Length > MaximumAssistantHistoryMessageLength
                    ? LongAssistantHistorySummary
                    : message.Content;
            if (totalLength + content.Length > MaximumPromptCharacters)
            {
                break;
            }

            prepared.Add(new ChatMessage(
                message.Role == "assistant" ? ChatRole.Assistant : ChatRole.User,
                content));
            totalLength += content.Length;
        }

        prepared.Reverse();
        return prepared;
    }

}
