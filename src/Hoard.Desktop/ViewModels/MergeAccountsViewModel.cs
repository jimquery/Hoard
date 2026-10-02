using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Data;
using Hoard.Core.Domain;
using Hoard.Core.Sync;

namespace Hoard.Desktop.ViewModels;

/// <summary>One flagged pair in the review grid. "Keep" defaults to the older account; the checkbox swaps it.</summary>
public sealed partial class DuplicatePairItem : ObservableObject
{
    private readonly DuplicateAccountPair _pair;

    public DuplicatePairItem(DuplicateAccountPair pair) => _pair = pair;

    public long ExistingId => _pair.Existing.Id;
    public long NewcomerId => _pair.Newcomer.Id;
    public string ExistingText => Describe(_pair.Existing, _pair.ExistingInstitution, _pair.ExistingTransactions);
    public string NewcomerText => Describe(_pair.Newcomer, _pair.NewcomerInstitution, _pair.NewcomerTransactions);

    /// <summary>Tick to keep the newer row instead (its id and settings survive; the older one's history folds in).</summary>
    [ObservableProperty] private bool _keepNewer;
    partial void OnKeepNewerChanged(bool value) { OnPropertyChanged(nameof(KeepText)); OnPropertyChanged(nameof(AbsorbText)); OnPropertyChanged(nameof(KeepId)); OnPropertyChanged(nameof(AbsorbId)); }

    public long KeepId => KeepNewer ? NewcomerId : ExistingId;
    public long AbsorbId => KeepNewer ? ExistingId : NewcomerId;
    public string KeepText => KeepNewer ? NewcomerText : ExistingText;
    public string AbsorbText => KeepNewer ? ExistingText : NewcomerText;
    public string KeepName => (KeepNewer ? _pair.Newcomer : _pair.Existing).EffectiveName;
    public string AbsorbName => (KeepNewer ? _pair.Existing : _pair.Newcomer).EffectiveName;

    private static string Describe(Account a, string institution, int transactions)
    {
        var name = a.EffectiveName + (string.IsNullOrWhiteSpace(a.DisplayName) ? "" : $" (bank: {a.Name})");
        var asOf = a.BalanceDateUtc is { } d ? d.ToLocalTime().ToString("g") : "never";
        return $"{name}\n{institution} · {a.Balance:N2} {a.Currency} as of {asOf} · {transactions:N0} transactions · added {a.CreatedAtUtc.ToLocalTime():d}";
    }
}

/// <summary>An account in the manual-merge pickers.</summary>
public sealed record MergeAccountChoice(long Id, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class MergeAccountsViewModel : ObservableObject
{
    private readonly AccountMergeService _merge;
    private readonly IAccountRepository _accounts;

    public MergeAccountsViewModel(AccountMergeService merge, IAccountRepository accounts)
    {
        _merge = merge;
        _accounts = accounts;
    }

    public ObservableCollection<DuplicatePairItem> Pairs { get; } = new();
    public ObservableCollection<MergeAccountChoice> AllAccounts { get; } = new();

    [ObservableProperty] private DuplicatePairItem? _selected;
    [ObservableProperty] private MergeAccountChoice? _manualKeep;
    [ObservableProperty] private MergeAccountChoice? _manualAbsorb;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    /// <summary>Something was merged, dismissed or flagged: the caller should refresh.</summary>
    public bool Changed { get; private set; }

    public bool HasPairs => Pairs.Count > 0;
    public string EmptyText => "No accounts are flagged. Scan all accounts to look for duplicates that were created before Hoard started flagging them, or merge any two accounts by hand below.";

    /// <summary>Set by the window: asks the user to confirm an irreversible merge. Returns true to proceed.</summary>
    public Func<string, bool>? Confirm { get; set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            await ReloadPairsAsync(await _merge.ListFlaggedAsync(ct));
            var summaries = await _accounts.ListSummariesAsync(includeHidden: true, ct);
            static string Bank(AccountSummary a) => string.IsNullOrWhiteSpace(a.OrgName) ? a.InstitutionName : a.OrgName;
            AllAccounts.Clear();
            foreach (var a in summaries.OrderBy(Bank).ThenBy(a => a.Name).ThenBy(a => a.Id))
                AllAccounts.Add(new MergeAccountChoice(a.Id, $"{Bank(a)} — {a.Name}" + (a.IsHidden ? " (hidden)" : "") + $"  [{a.Balance:N2} {a.Currency}]"));
            ManualKeep = null;
            ManualAbsorb = null;
        }
        finally { IsBusy = false; }
    }

    private Task ReloadPairsAsync(IReadOnlyList<DuplicateAccountPair> pairs)
    {
        var selectedNewcomer = Selected?.NewcomerId;
        Pairs.Clear();
        foreach (var p in pairs) Pairs.Add(new DuplicatePairItem(p));
        Selected = Pairs.FirstOrDefault(p => p.NewcomerId == selectedNewcomer) ?? Pairs.FirstOrDefault();
        OnPropertyChanged(nameof(HasPairs));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        try
        {
            var before = Pairs.Count;
            await ReloadPairsAsync(await _merge.ScanAsync());
            var found = Pairs.Count - before;
            Status = found > 0 ? $"Scan flagged {found} more pair(s)." : "Scan found nothing new.";
            if (found > 0) Changed = true;
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task MergeSelectedAsync() => Selected is { } p ? MergeAsync(p.KeepId, p.AbsorbId, p.KeepName, p.AbsorbName) : Task.CompletedTask;

    [RelayCommand]
    private async Task DismissSelectedAsync()
    {
        if (Selected is not { } p) return;
        IsBusy = true;
        try
        {
            await _merge.DismissAsync(p.NewcomerId);
            Changed = true;
            Status = $"\"{p.AbsorbName}\" and \"{p.KeepName}\" are marked as different accounts.";
            await LoadAsync();
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task MergeManualAsync()
    {
        if (ManualKeep is null || ManualAbsorb is null) { Status = "Pick both accounts."; return Task.CompletedTask; }
        if (ManualKeep.Id == ManualAbsorb.Id) { Status = "Pick two different accounts."; return Task.CompletedTask; }
        return MergeAsync(ManualKeep.Id, ManualAbsorb.Id, ManualKeep.Label, ManualAbsorb.Label);
    }

    private async Task MergeAsync(long keepId, long absorbId, string keepName, string absorbName)
    {
        var question = $"Merge \"{absorbName}\" into \"{keepName}\"?\n\n" +
                       $"\"{keepName}\" keeps its settings and history. Transactions it does not already have are moved over from " +
                       $"\"{absorbName}\", which is then deleted. If \"{absorbName}\" is the copy your bank connection currently feeds, " +
                       $"\"{keepName}\" takes over that connection so future syncs update it.\n\nThis cannot be undone.";
        if (Confirm is not null && !Confirm(question)) return;
        IsBusy = true;
        try
        {
            var r = await _merge.MergeAsync(keepId, absorbId);
            Changed = true;
            Status = $"Merged \"{absorbName}\" into \"{keepName}\": {r.TransactionsMoved:N0} transaction(s) moved, {r.TransactionsDropped:N0} already present, " +
                     $"{r.SnapshotsMoved:N0} balance day(s) added" + (r.IdentityTaken ? "; the survivor now receives the bank feed." : ".");
            await LoadAsync();
        }
        catch (Exception ex) { Status = "Merge failed: " + ex.Message; }
        finally { IsBusy = false; }
    }
}
