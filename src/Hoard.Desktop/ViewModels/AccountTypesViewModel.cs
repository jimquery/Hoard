using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Data;
using Hoard.Core.Domain;

namespace Hoard.Desktop.ViewModels;

/// <summary>Editable row for the account-types manager; edits persist immediately.</summary>
public sealed partial class AccountTypeItem : ObservableObject
{
    private readonly AccountCategory _model;
    private readonly IAccountRepository _repo;

    public AccountTypeItem(AccountCategory model, int accountCount, IAccountRepository repo)
    {
        _model = model;
        _repo = repo;
        AccountCount = accountCount;
        _name = model.Name;
        _isLiability = model.Kind == AccountKind.Liability;
        _color = model.Color ?? "#8a8986";
        _includeInSpending = model.IncludeInSpending;
    }

    public long Id => _model.Id;
    public bool IsSystem => _model.IsSystem;
    public int AccountCount { get; }
    public string KindText => IsLiability ? "Liability" : "Asset";

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isLiability;
    [ObservableProperty] private string _color;
    /// <summary>Counts in the spending and income-vs-expense reports (Cash and Credit Card by default).</summary>
    [ObservableProperty] private bool _includeInSpending;

    partial void OnIncludeInSpendingChanged(bool value) => Persist();
    partial void OnNameChanged(string value) => Persist();
    partial void OnIsLiabilityChanged(bool value) { OnPropertyChanged(nameof(KindText)); Persist(); }
    partial void OnColorChanged(string value) => Persist();

    private void Persist()
    {
        if (string.IsNullOrWhiteSpace(Name)) return;
        _model.Name = Name.Trim();
        _model.Kind = IsLiability ? AccountKind.Liability : AccountKind.Asset;
        _model.Color = Services.ThemeService.TryParseColor(Color) is null ? _model.Color : Color;
        _model.IncludeInSpending = IncludeInSpending;
        _ = _repo.UpdateCategoryAsync(_model);
    }
}

public sealed partial class AccountTypesViewModel : ObservableObject
{
    private readonly IAccountRepository _repo;

    public AccountTypesViewModel(IAccountRepository repo) => _repo = repo;

    public static IReadOnlyList<string> ColorChoices { get; } =
        ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#4a3aa7", "#e34948", "#8a8986", "#0d366b", "#7a4b12", "#3d7a5c"];

    public ObservableCollection<AccountTypeItem> Types { get; } = new();
    [ObservableProperty] private AccountTypeItem? _selected;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private bool _newIsLiability;
    [ObservableProperty] private string _status = "";
    public bool Changed { get; private set; }

    public async Task LoadAsync()
    {
        var selectedId = Selected?.Id;
        Types.Clear();
        foreach (var c in await _repo.ListCategoriesAsync())
            Types.Add(new AccountTypeItem(c, await _repo.CountAccountsOfCategoryAsync(c.Id), _repo));
        Selected = Types.FirstOrDefault(t => t.Id == selectedId);
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var name = NewName.Trim();
        if (name.Length == 0) { Status = "Enter a name for the new type."; return; }
        if (Types.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) { Status = "A type with that name already exists."; return; }
        var used = Types.Select(t => t.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var color = ColorChoices.FirstOrDefault(c => !used.Contains(c)) ?? ColorChoices[^1];
        await _repo.InsertCategoryAsync(new AccountCategory { Name = name, Kind = NewIsLiability ? AccountKind.Liability : AccountKind.Asset, Color = color });
        NewName = "";
        Changed = true;
        Status = $"Added \"{name}\".";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is null) return;
        var t = Selected;
        await _repo.DeleteCategoryAsync(t.Id);
        Changed = true;
        Status = t.AccountCount > 0 ? $"Deleted \"{t.Name}\"; {t.AccountCount} account(s) are now untyped." : $"Deleted \"{t.Name}\".";
        await LoadAsync();
    }

    [RelayCommand]
    private Task MoveUpAsync() => MoveAsync(-1);

    [RelayCommand]
    private Task MoveDownAsync() => MoveAsync(1);

    private async Task MoveAsync(int delta)
    {
        if (Selected is null) return;
        var index = Types.IndexOf(Selected);
        var target = index + delta;
        if (target < 0 || target >= Types.Count) return;
        Types.Move(index, target);
        await _repo.ReorderCategoriesAsync(Types.Select(t => t.Id).ToList());
        Changed = true;
    }

    public void MarkChanged() => Changed = true;
}
