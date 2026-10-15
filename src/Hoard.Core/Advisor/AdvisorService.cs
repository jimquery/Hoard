using Anthropic.Models.Beta.Messages;
using Hoard.Core.Configuration;
using Hoard.Core.Security;
using Microsoft.Extensions.Logging;

namespace Hoard.Core.Advisor;

/// <summary>
/// Settings and start-up for the AI advisor. The advisor is off until the user has read what it shares, accepted, and
/// entered an Anthropic API key; the key is sealed in the database like a bank credential, the other choices live in
/// hoard.json.
/// </summary>
public sealed class AdvisorService
{
    public const string ApiKeySetting = "advisor.anthropic_api_key";

    private readonly HoardSettings _settings;
    private readonly SecretSettings _secrets;
    private readonly FinancialToolbox _toolbox;
    private readonly ILoggerFactory _logs;

    public AdvisorService(HoardSettings settings, SecretSettings secrets, FinancialToolbox toolbox, ILoggerFactory logs)
    {
        _settings = settings;
        _secrets = secrets;
        _toolbox = toolbox;
        _logs = logs;
    }

    public AdvisorSettings Settings => _settings.Advisor;

    public Task<bool> HasApiKeyAsync(CancellationToken ct = default) => _secrets.HasAsync(ApiKeySetting, ct);

    public async Task<bool> IsReadyAsync(CancellationToken ct = default) => _settings.Advisor.Accepted && await HasApiKeyAsync(ct);

    /// <summary>Saves the choices; a null <paramref name="apiKey"/> keeps the stored key, a blank one deletes it.</summary>
    public async Task SaveAsync(bool accepted, bool allowTransactionDetail, string effort, string? apiKey, CancellationToken ct = default)
    {
        if (apiKey is not null) await _secrets.SetAsync(ApiKeySetting, apiKey, ct);
        _settings.Advisor.Accepted = accepted;
        _settings.Advisor.AllowTransactionDetail = allowTransactionDetail;
        _settings.Advisor.Effort = AdvisorSettings.Efforts.Contains(effort) ? effort : "high";
        SettingsStore.Save(_settings);
    }

    /// <summary>Forgets the key and the consent.</summary>
    public async Task TurnOffAsync(CancellationToken ct = default)
    {
        await _secrets.SetAsync(ApiKeySetting, null, ct);
        _settings.Advisor.Accepted = false;
        SettingsStore.Save(_settings);
    }

    public async Task<AdvisorAgent> StartConversationAsync(CancellationToken ct = default)
    {
        if (!_settings.Advisor.Accepted) throw new AdvisorException("Turn the advisor on first (Advisor › Settings).");
        var key = await _secrets.GetAsync(ApiKeySetting, ct) ?? throw new AdvisorException("Enter an Anthropic API key first (Advisor › Settings).");
        var options = new AdvisorOptions(
            string.IsNullOrWhiteSpace(_settings.Advisor.Model) ? AdvisorAgent.DefaultModel : _settings.Advisor.Model,
            _settings.Advisor.Effort switch { "low" => Effort.Low, "medium" => Effort.Medium, _ => Effort.High },
            _settings.Advisor.AllowTransactionDetail);
        return new AdvisorAgent(key, _toolbox, options, _logs.CreateLogger<AdvisorAgent>());
    }
}
