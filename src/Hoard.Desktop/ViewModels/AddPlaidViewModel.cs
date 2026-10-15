using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Providers.Plaid;
using Hoard.Core.Sync;

namespace Hoard.Desktop.ViewModels;

/// <summary>A choice in the "Hoard account" column: create a new account (Id 0) or take over an existing one.</summary>
public sealed record LinkTargetOption(long Id, string Label)
{
    public const long NewAccountId = 0;
    public static readonly LinkTargetOption NewAccount = new(NewAccountId, "➕ Create a new account");
    public override string ToString() => Label;
}

/// <summary>One Plaid account in the linking step, with the Hoard account it will become.</summary>
public sealed partial class PlaidAccountLinkRow : ObservableObject
{
    public PlaidAccountLinkRow(PlaidAccountInfo account, IReadOnlyList<LinkTargetOption> options, long suggestedId)
    {
        Account = account;
        Options = options;
        _target = options.FirstOrDefault(o => o.Id == suggestedId) ?? LinkTargetOption.NewAccount;
        WasSuggested = suggestedId != LinkTargetOption.NewAccountId;
    }

    public PlaidAccountInfo Account { get; }
    public IReadOnlyList<LinkTargetOption> Options { get; }
    public bool WasSuggested { get; }

    public string Name => PlaidClient.DisplayName(Account);
    public string TypeText => string.Join(" · ", new[] { Account.Type, Account.Subtype }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string BalanceText => Account.Balance is { } b ? b.ToString("C", CultureInfo.CurrentCulture) : "";

    [ObservableProperty] private LinkTargetOption _target;
}

/// <summary>
/// Adds a bank through Plaid: app keys → Plaid Link in the browser (Hosted Link) → choose, per Plaid account, a new
/// Hoard account or an existing one to take over (keeping its history) → save.
/// </summary>
public sealed partial class AddPlaidViewModel : ObservableObject
{
    public const string PlaidDashboardUrl = "https://dashboard.plaid.com/developers/keys";

    private readonly InstitutionService _service;
    private CancellationTokenSource? _waitCts;
    private PlaidAppKeys? _keys;
    private PendingPlaidConnection? _pending;

    public AddPlaidViewModel(InstitutionService service) => _service = service;

    public IReadOnlyList<PlaidEnvironment> Environments { get; } = [PlaidEnvironment.Production, PlaidEnvironment.Sandbox];

    [ObservableProperty] private PlaidEnvironment _environment = PlaidEnvironment.Production;
    [ObservableProperty] private string _clientId = "";
    /// <summary>Set from the PasswordBox in code-behind.</summary>
    public string Secret { get; set; } = "";
    [ObservableProperty] private bool _hasSavedSecret;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isWaitingForBrowser;
    [ObservableProperty] private string? _hostedLinkUrl;
    /// <summary>False while entering keys and linking; true on the account-linking step.</summary>
    [ObservableProperty] private bool _isChoosingAccounts;
    [ObservableProperty] private bool _succeeded;

    public ObservableCollection<PlaidAccountLinkRow> Rows { get; } = new();

    private PlaidAppKeys? _savedKeys;

    public async Task LoadAsync()
    {
        try
        {
            _savedKeys = await _service.GetSavedPlaidKeysAsync();
            if (_savedKeys is null) return;
            Environment = _savedKeys.Environment;
            ClientId = _savedKeys.ClientId;
            HasSavedSecret = true;
        }
        catch (Exception ex)
        {
            Status = "Could not read the saved Plaid keys: " + ex.Message;
        }
    }

    private PlaidAppKeys CurrentKeys()
    {
        var secret = string.IsNullOrEmpty(Secret) && _savedKeys is not null && _savedKeys.Environment == Environment && _savedKeys.ClientId == ClientId.Trim()
            ? _savedKeys.Secret
            : Secret.Trim();
        return new PlaidAppKeys(Environment, ClientId.Trim(), secret);
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        IsBusy = true;
        Status = "Starting Plaid Link...";
        try
        {
            _keys = CurrentKeys();
            var session = await _service.StartPlaidLinkAsync(_keys);
            HostedLinkUrl = session.HostedLinkUrl;
            OpenBrowser();
            IsWaitingForBrowser = true;
            Status = "Plaid Link is open in your browser. Choose your bank and sign in there; this window continues when you finish.";

            _waitCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var result = await _service.WaitForPlaidLinkAsync(_keys, session.LinkToken, ct: _waitCts.Token);
            IsWaitingForBrowser = false;
            if (result.State != PlaidLinkState.Succeeded)
            {
                Status = result.Message ?? "Plaid Link was closed before a bank was linked.";
                return;
            }

            Status = $"Linked {result.InstitutionName ?? "the bank"}. Reading its accounts...";
            _pending = await _service.FinishPlaidLinkAsync(_keys, result);
            ShowAccounts(_pending);
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped waiting for Plaid Link.";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsWaitingForBrowser = false;
            IsBusy = false;
            _waitCts?.Dispose();
            _waitCts = null;
        }
    }

    private void ShowAccounts(PendingPlaidConnection pending)
    {
        var options = new List<LinkTargetOption> { LinkTargetOption.NewAccount };
        options.AddRange(pending.Candidates
            .OrderBy(c => c.InstitutionName).ThenBy(c => c.Name)
            .Select(c => new LinkTargetOption(c.Id, $"{c.InstitutionName} — {c.Name}  ({c.Balance.ToString("C", CultureInfo.CurrentCulture)})")));

        Rows.Clear();
        foreach (var a in pending.Accounts)
            Rows.Add(new PlaidAccountLinkRow(a, options, pending.Suggestions.GetValueOrDefault(a.AccountId)));

        if (string.IsNullOrWhiteSpace(Name)) Name = pending.Credential.InstitutionName is { Length: > 0 } n ? $"{n} (Plaid)" : "Plaid";
        IsChoosingAccounts = true;
        var suggested = Rows.Count(r => r.WasSuggested);
        Status = suggested > 0
            ? $"{suggested} of {Rows.Count} account(s) look like accounts Hoard already has and are pre-selected. Check each choice, then Save."
            : "Choose, for each account, whether it is new or one Hoard already has, then Save.";
    }

    [RelayCommand]
    private void OpenBrowser()
    {
        if (HostedLinkUrl is null) return;
        Process.Start(new ProcessStartInfo(HostedLinkUrl) { UseShellExecute = true });
    }

    [RelayCommand]
    private void StopWaiting() => _waitCts?.Cancel();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_pending is null) return;
        if (string.IsNullOrWhiteSpace(Name)) { Status = "Enter a name for this connection."; return; }
        var duplicate = Rows.Where(r => r.Target.Id != LinkTargetOption.NewAccountId).GroupBy(r => r.Target.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) { Status = $"\"{duplicate.First().Target.Label}\" is chosen for more than one Plaid account."; return; }

        IsBusy = true;
        Status = "Saving...";
        try
        {
            var links = Rows.ToDictionary(r => r.Account.AccountId, r => r.Target.Id == LinkTargetOption.NewAccountId ? (long?)null : r.Target.Id);
            var inst = await _service.CommitPlaidConnectionAsync(_pending, Name, links);
            _pending = null;
            var linked = links.Values.Count(v => v is not null);
            Status = linked > 0
                ? $"Added \"{inst.Name}\". {linked} existing account(s) now sync through Plaid with their history intact; the old connection will skip them. Run a sync to download transactions."
                : $"Added \"{inst.Name}\". Run a sync to download transactions.";
            Succeeded = true;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Called when the window closes: stops any wait and deletes an Item that was linked but never saved.</summary>
    public async Task CloseAsync()
    {
        _waitCts?.Cancel();
        if (_pending is { } pending)
        {
            _pending = null;
            await _service.DiscardPlaidConnectionAsync(pending);
        }
    }
}
