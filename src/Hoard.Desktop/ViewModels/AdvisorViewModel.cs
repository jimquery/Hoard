using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Advisor;
using Hoard.Core.Configuration;

namespace Hoard.Desktop.ViewModels;

public enum AdvisorMessageKind { User, Assistant, Step, Error }

/// <summary>One entry in the advisor conversation: a question, an answer (Markdown), a data-reading step, or an error.</summary>
public sealed partial class AdvisorMessage : ObservableObject
{
    public AdvisorMessageKind Kind { get; init; }
    [ObservableProperty] private string _text = "";
    public string? Footer { get; init; }
    public bool IsUser => Kind == AdvisorMessageKind.User;
    public bool IsAssistant => Kind == AdvisorMessageKind.Assistant;
    public bool IsStep => Kind == AdvisorMessageKind.Step;
    public bool IsError => Kind == AdvisorMessageKind.Error;
}

public sealed record AdvisorPrompt(string Title, string Question);

/// <summary>
/// The AI Advisor page. Until the user has read what is shared and entered an Anthropic API key, it shows the settings
/// panel; afterwards, a conversation with Claude, which reads the household's figures through Hoard's tools.
/// </summary>
public sealed partial class AdvisorViewModel : ObservableObject
{
    // Claude Opus 5.5 list prices per million tokens, for the cost estimate under each answer.
    private const decimal InputPrice = 4.00m, OutputPrice = 20.00m, CacheReadPrice = 0.20m, CacheWritePrice = 5.00m;

    private readonly AdvisorService _service;
    private AdvisorAgent? _agent;
    private CancellationTokenSource? _cts;

    public AdvisorViewModel(AdvisorService service)
    {
        _service = service;
        _allowTransactionDetail = service.Settings.AllowTransactionDetail;
        _effort = service.Settings.Effort;
        _accepted = service.Settings.Accepted;
    }

    public ObservableCollection<AdvisorMessage> Messages { get; } = new();

    public IReadOnlyList<AdvisorPrompt> Prompts { get; } =
    [
        new("Financial check-up", "Give me a complete financial check-up: where my money goes, how my savings rate and net worth are trending, and the five changes that would help most, with the dollar impact of each."),
        new("Cut my spending", "Where can I realistically cut spending? Look at my categories, trends, merchants and subscriptions, and rank the opportunities by how much they would save per year."),
        new("Budget review", "How am I doing against my budget this month and over the last few months? Which targets are unrealistic, and what targets would you set instead?"),
        new("Subscriptions audit", "Review my recurring charges and subscriptions. Which look forgotten, duplicated or worth renegotiating, and what would cancelling them save?"),
        new("Grow net worth", "How can I grow my net worth faster? Consider my savings rate, cash on hand, debt, and retirement and college accounts."),
        new("Emergency fund & debt", "Do I have enough of an emergency fund for my spending level, and should I be paying down any debt faster?"),
    ];

    public IReadOnlyList<string> EffortOptions => AdvisorSettings.Efforts;

    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private bool _accepted;
    [ObservableProperty] private bool _allowTransactionDetail;
    [ObservableProperty] private string _effort;
    [ObservableProperty] private bool _hasApiKey;
    /// <summary>Set from the PasswordBox; null = leave the stored key unchanged.</summary>
    public string? NewApiKey { get; set; }
    [ObservableProperty] private string _settingsStatus = "";

    [ObservableProperty] private string _question = "";
    [ObservableProperty] private bool _isThinking;
    [ObservableProperty] private string _sessionCost = "";
    public bool HasMessages => Messages.Count > 0;

    public async Task LoadAsync()
    {
        HasApiKey = await _service.HasApiKeyAsync();
        IsReady = await _service.IsReadyAsync();
        ShowSettings = !IsReady;
    }

    [RelayCommand]
    private void OpenSettings() => ShowSettings = true;

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (!Accepted) { SettingsStatus = "Tick the box to confirm you understand what the advisor sends to Anthropic."; return; }
        if (!HasApiKey && string.IsNullOrWhiteSpace(NewApiKey)) { SettingsStatus = "Enter an Anthropic API key (console.anthropic.com › API keys)."; return; }
        try
        {
            await _service.SaveAsync(Accepted, AllowTransactionDetail, Effort, string.IsNullOrWhiteSpace(NewApiKey) ? null : NewApiKey);
            NewApiKey = null;
            _agent = null; // new options apply to the next conversation
            await LoadAsync();
            SettingsStatus = "";
        }
        catch (Exception ex) { SettingsStatus = ex.Message; }
    }

    [RelayCommand]
    private async Task TurnOffAsync()
    {
        await _service.TurnOffAsync();
        Accepted = false;
        _agent = null;
        await LoadAsync();
        SettingsStatus = "The advisor is off and the API key has been deleted.";
    }

    [RelayCommand]
    private void NewConversation()
    {
        _cts?.Cancel();
        _agent = null;
        Messages.Clear();
        SessionCost = "";
        _sessionDollars = 0;
        OnPropertyChanged(nameof(HasMessages));
    }

    [RelayCommand]
    private Task AskPromptAsync(AdvisorPrompt prompt)
    {
        Question = prompt.Question;
        return AskAsync();
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    private decimal _sessionDollars;

    [RelayCommand]
    private async Task AskAsync()
    {
        var question = Question.Trim();
        if (question.Length == 0 || IsThinking) return;
        Question = "";
        Messages.Add(new AdvisorMessage { Kind = AdvisorMessageKind.User, Text = question });
        OnPropertyChanged(nameof(HasMessages));
        IsThinking = true;
        _cts = new CancellationTokenSource();
        var steps = new List<AdvisorMessage>();
        var progress = new Progress<AdvisorEvent>(e =>
        {
            switch (e)
            {
                case AdvisorToolCalled call:
                    var step = new AdvisorMessage { Kind = AdvisorMessageKind.Step, Text = call.Description };
                    steps.Add(step);
                    Messages.Add(step);
                    break;
            }
        });
        try
        {
            _agent ??= await _service.StartConversationAsync();
            var answer = await _agent.AskAsync(question, progress, _cts.Token);
            var usage = answer.Usage;
            var dollars = (usage.InputTokens * InputPrice + usage.OutputTokens * OutputPrice
                         + usage.CacheReadTokens * CacheReadPrice + usage.CacheWriteTokens * CacheWritePrice) / 1_000_000m;
            _sessionDollars += dollars;
            SessionCost = $"This conversation: about {_sessionDollars:C2}";
            Messages.Add(new AdvisorMessage
            {
                Kind = AdvisorMessageKind.Assistant,
                Text = answer.Markdown,
                Footer = $"Read your data {usage.ToolCalls} time(s) · about {dollars:C2}",
            });
        }
        catch (OperationCanceledException)
        {
            Messages.Add(new AdvisorMessage { Kind = AdvisorMessageKind.Error, Text = "Stopped." });
        }
        catch (Exception ex)
        {
            Messages.Add(new AdvisorMessage { Kind = AdvisorMessageKind.Error, Text = ex.Message });
        }
        finally
        {
            // The step list was progress for this answer; collapse it to one line so the conversation stays readable.
            foreach (var s in steps) Messages.Remove(s);
            if (steps.Count > 0)
            {
                var index = Messages.Count - 1;
                Messages.Insert(Math.Max(0, index), new AdvisorMessage
                {
                    Kind = AdvisorMessageKind.Step,
                    Text = "Looked at: " + string.Join(" · ", steps.Select(s => s.Text).Distinct()),
                });
            }
            IsThinking = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>The conversation as Markdown, for saving.</summary>
    public string ExportMarkdown()
    {
        var sb = new StringBuilder($"# Hoard advisor — {DateTime.Now:MMMM d, yyyy}\n\n");
        foreach (var m in Messages)
        {
            if (m.IsUser) sb.Append("## ").Append(m.Text.Replace("\n", " ")).Append("\n\n");
            else if (m.IsAssistant) sb.Append(m.Text).Append("\n\n");
        }
        sb.Append("---\n*Generated by Claude from this household's data in Hoard. General information, not regulated financial advice.*\n");
        return sb.ToString();
    }
}
