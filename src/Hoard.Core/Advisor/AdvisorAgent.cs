using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;

namespace Hoard.Core.Advisor;

/// <summary>Something the advisor did while answering, for the UI to show as it happens.</summary>
public abstract record AdvisorEvent;
/// <summary>The advisor called a tool to read the household's data.</summary>
public sealed record AdvisorToolCalled(string ToolName, string Description) : AdvisorEvent;
/// <summary>What one answer cost, from the API's usage figures.</summary>
public sealed record AdvisorUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, int ApiCalls, int ToolCalls);

/// <summary>The advisor's answer (Markdown) and what it took.</summary>
public sealed record AdvisorAnswer(string Markdown, AdvisorUsage Usage);

/// <summary>
/// The AI advisor: a conversation with Claude in which Claude reads the household's figures through
/// <see cref="FinancialToolbox"/> tools (executed here, on this PC, against Hoard's database) and answers with analysis
/// and recommendations. One instance is one conversation; history is append-only, so each request re-sends the
/// previous one unchanged plus the new turn (prompt caching then makes the repeated prefix cheap, and Claude's thinking
/// blocks stay valid). What leaves the PC is the user's questions and the tool results; nothing else.
/// </summary>
public sealed class AdvisorAgent
{
    public const string DefaultModel = "claude-opus-5-5";
    /// <summary>
    /// Server-side fallback: if Claude Opus 5.5's safety classifiers decline a request, the API re-serves it on
    /// Anthropic's recommended fallback model (chosen by refusal category) within the same call.
    /// </summary>
    public const string FallbackBeta = "server-side-fallback-2026-07-01";
    private const int MaxApiCallsPerQuestion = 16;
    private const long MaxTokens = 16000;

    public const string SystemPrompt = """
        You are the financial analyst built into Hoard, a personal-finance app that keeps a household's bank, card,
        investment, loan and property data on their own PC. You help this household understand their money and decide
        what to do next: where spending can be cut, how to save more, how to grow net worth, how to pay down debt, and how
        their budget is tracking.

        How to work:
        - Ground every figure you state in a tool result from this conversation. Start with get_financial_overview, then
          pull whatever else the question needs (cash flow, categories, trends, the budget, subscriptions, merchants,
          individual transactions when allowed). Do not guess numbers you can look up; if the data cannot answer something,
          say what is missing.
        - Be specific to this household. "Dining out averaged $612 a month over the last year, up about $40 a month since
          spring" is useful; "consider eating out less" is not. Attach an estimated monthly or annual dollar impact to each
          recommendation and rank recommendations by impact.
        - Distinguish fixed costs (housing, insurance, loan payments) from discretionary spending, and recurring
          subscriptions from one-off purchases. Point out unusual spikes, rising trends and categories over budget.
        - For saving and net-worth questions, consider the savings rate, an emergency fund (months of expenses held in
          cash), high-interest debt, retirement and college accounts, and the household members' ages.
        - The data only shows what flows through connected accounts. Payroll deductions (401(k), insurance, taxes) are
          invisible, transfers between accounts are excluded from spending, and some transactions may be uncategorized;
          mention these limits when they affect a conclusion.
        - You give general financial education and analysis of their own numbers, not regulated investment, tax or legal
          advice. When a decision really needs a professional (choosing specific securities, tax strategy, estate
          planning), say so once, briefly.

        Format answers in Markdown: a one or two sentence summary first, then short sections with headings, bullet points
        and small tables where they help. Use the household's currency with no decimals for amounts over $100. Keep it
        tight; the user can ask follow-up questions.
        """;

    private readonly AnthropicClient _client;
    private readonly FinancialToolbox _toolbox;
    private readonly ILogger _log;
    private readonly List<BetaMessageParam> _messages = new();
    private readonly List<BetaToolUnion> _tools;

    public AdvisorAgent(string apiKey, FinancialToolbox toolbox, AdvisorOptions options, ILogger log)
    {
        _client = new AnthropicClient { ApiKey = apiKey };
        _toolbox = toolbox;
        _log = log;
        Options = options;
        _tools = FinancialToolbox.DefinitionsFor(options.AllowTransactionDetail).Select(ToTool).ToList();
    }

    public AdvisorOptions Options { get; }
    public bool HasHistory => _messages.Count > 0;

    /// <summary>
    /// Sends a question and runs the tool loop until Claude answers. Tool calls are reported through
    /// <paramref name="progress"/> as they happen.
    /// </summary>
    public async Task<AdvisorAnswer> AskAsync(string question, IProgress<AdvisorEvent>? progress = null, CancellationToken ct = default)
    {
        var turnStart = _messages.Count;
        _messages.Add(new BetaMessageParam { Role = Role.User, Content = question });
        try
        {
            return await RunTurnAsync(progress, ct);
        }
        catch
        {
            // A failed, refused or cancelled question leaves no trace, so the conversation stays valid for the next one.
            _messages.RemoveRange(turnStart, _messages.Count - turnStart);
            throw;
        }
    }

    private async Task<AdvisorAnswer> RunTurnAsync(IProgress<AdvisorEvent>? progress, CancellationToken ct)
    {
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        var calls = 0;
        var toolCalls = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (++calls > MaxApiCallsPerQuestion)
                throw new AdvisorException("The advisor took too many steps without finishing an answer. Try a narrower question.");

            BetaMessage response;
            try
            {
                response = await _client.Beta.Messages.Create(new MessageCreateParams
                {
                    Model = Options.Model,
                    MaxTokens = MaxTokens,
                    System = SystemPrompt,
                    Tools = _tools,
                    Messages = _messages,
                    OutputConfig = new BetaOutputConfig { Effort = Options.Effort },
                    // Automatic prompt caching: the system prompt, tools and the conversation so far are re-read from
                    // cache on every step of the loop and every follow-up question.
                    CacheControl = new BetaCacheControlEphemeral(),
                    Fallbacks = new Default(), // serialized as "default"
                    Betas = [FallbackBeta],
                }, ct);
            }
            catch (AnthropicApiException ex)
            {
                throw new AdvisorException(Explain(ex), ex);
            }

            input += response.Usage.InputTokens;
            output += response.Usage.OutputTokens;
            cacheRead += response.Usage.CacheReadInputTokens ?? 0;
            cacheWrite += response.Usage.CacheCreationInputTokens ?? 0;

            if (response.StopReason == "refusal")
            {
                throw new AdvisorException("Claude declined to answer this request. Try rephrasing the question.");
            }

            // Echo the whole assistant turn back unchanged (thinking blocks included) and run any tool calls.
            var assistant = new List<BetaContentBlockParam>();
            var results = new List<BetaContentBlockParam>();
            var text = new List<string>();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var t))
                {
                    assistant.Add(new BetaTextBlockParam { Text = t.Text });
                    text.Add(t.Text);
                }
                else if (block.TryPickThinking(out var th))
                {
                    assistant.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                }
                else if (block.TryPickRedactedThinking(out var rt))
                {
                    assistant.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
                }
                else if (block.TryPickToolUse(out var tu))
                {
                    assistant.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                    toolCalls++;
                    progress?.Report(new AdvisorToolCalled(tu.Name, Describe(tu.Name, tu.Input)));
                    results.Add(await RunToolAsync(tu, ct));
                }
            }
            _messages.Add(new BetaMessageParam { Role = Role.Assistant, Content = assistant });

            if (results.Count > 0 && response.StopReason == "tool_use")
            {
                // All results of one turn go back together in a single user message.
                _messages.Add(new BetaMessageParam { Role = Role.User, Content = results });
                continue;
            }

            var answer = string.Join("\n\n", text).Trim();
            if (response.StopReason == "max_tokens")
                answer += "\n\n*(The answer was cut off because it reached the length limit. Ask me to continue.)*";
            _log.LogInformation("Advisor answered in {Calls} API call(s), {Tools} tool call(s); tokens in {In} (cache read {Read}, write {Write}), out {Out}",
                calls, toolCalls, input, cacheRead, cacheWrite, output);
            return new AdvisorAnswer(answer.Length > 0 ? answer : "*(No answer was returned.)*",
                new AdvisorUsage(input, output, cacheRead, cacheWrite, calls, toolCalls));
        }
    }

    private async Task<BetaToolResultBlockParam> RunToolAsync(BetaToolUseBlock call, CancellationToken ct)
    {
        try
        {
            var json = await _toolbox.ExecuteAsync(call.Name, call.Input, Options.AllowTransactionDetail, ct);
            return new BetaToolResultBlockParam { ToolUseID = call.ID, Content = json };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Advisor tool {Tool} failed", call.Name);
            return new BetaToolResultBlockParam { ToolUseID = call.ID, Content = "Error: " + ex.Message, IsError = true };
        }
    }

    private static BetaToolUnion ToTool(AdvisorToolDefinition d)
    {
        var schema = d.InputSchema;
        var properties = new Dictionary<string, JsonElement>();
        if (schema["properties"] is System.Text.Json.Nodes.JsonObject props)
            foreach (var (name, value) in props)
                properties[name] = JsonSerializer.SerializeToElement(value);
        var required = schema["required"] is System.Text.Json.Nodes.JsonArray req
            ? req.Select(r => r!.GetValue<string>()).ToList()
            : [];
        return new BetaTool
        {
            Name = d.Name,
            Description = d.Description,
            InputSchema = new() { Properties = properties, Required = required },
        };
    }

    /// <summary>A short, human description of a tool call for the progress list.</summary>
    public static string Describe(string tool, IReadOnlyDictionary<string, JsonElement> input)
    {
        string? Arg(string k) => input.TryGetValue(k, out var v) ? v.ToString() : null;
        return tool switch
        {
            "get_financial_overview" => "Reading balances, net worth and the last 12 months of cash flow",
            "get_monthly_cash_flow" => $"Reading monthly income and expenses ({Arg("months") ?? "12"} months)",
            "get_spending_by_category" => $"Reading spending by category, {Arg("from_month")} to {Arg("to_month")}",
            "get_spending_trend" => $"Reading the spending trend by category group ({Arg("months") ?? "12"} months)",
            "get_budget" => $"Reading the budget for {Arg("month") ?? "this month"}",
            "get_net_worth_history" => $"Reading net-worth history ({Arg("months") ?? "24"} months)",
            "get_subscriptions" => "Reading recurring charges",
            "get_top_merchants" => $"Finding the biggest merchants, {Arg("from_month")} to {Arg("to_month")}",
            "search_transactions" => "Searching transactions" + (Arg("text") is { } t ? $" for \"{t}\"" : "") + (Arg("category") is { } c ? $" in {c}" : ""),
            _ => tool,
        };
    }

    private static string Explain(AnthropicApiException ex) => ex switch
    {
        AnthropicUnauthorizedException => "The Anthropic API key was rejected. Check it under Advisor › Settings.",
        AnthropicRateLimitException => "The Anthropic API is rate-limiting this key. Wait a minute and try again.",
        _ when ex.Message.Contains("credit balance", StringComparison.OrdinalIgnoreCase) => "The Anthropic account has no credit left. Add credit at console.anthropic.com.",
        _ => "The Anthropic API returned an error: " + ex.Message,
    };
}

/// <summary>How the advisor runs.</summary>
/// <param name="AllowTransactionDetail">Offer the tools that share merchant names and individual transactions.</param>
public sealed record AdvisorOptions(string Model, Effort Effort, bool AllowTransactionDetail);

public sealed class AdvisorException : Exception
{
    public AdvisorException(string message, Exception? inner = null) : base(message, inner) { }
}
