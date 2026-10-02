using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Data;
using Hoard.Core.Domain;

namespace Hoard.Desktop.ViewModels;

/// <summary>Editable household member row; edits persist immediately.</summary>
public sealed partial class HouseholdMemberItem : ObservableObject
{
    private readonly HouseholdMember _model;
    private readonly IHouseholdRepository _repo;

    public HouseholdMemberItem(HouseholdMember model, int accountCount, IHouseholdRepository repo)
    {
        _model = model;
        _repo = repo;
        AccountCount = accountCount;
        _name = model.Name;
        _dateOfBirth = model.DateOfBirth;
        _relationship = model.Relationship ?? "";
    }

    public long Id => _model.Id;
    public int AccountCount { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private DateTime? _dateOfBirth;
    [ObservableProperty] private string _relationship;

    public string AgeText => DateOfBirth is { } dob
        ? $"{AgeCalculator.AgeOn(DateOnly.FromDateTime(dob), DateOnly.FromDateTime(DateTime.Today))}"
        : "";

    public string Turns18Text => DateOfBirth is { } dob ? AgeCalculator.BirthdayAt(DateOnly.FromDateTime(dob), 18).ToString("MMM d, yyyy") : "";

    partial void OnNameChanged(string value) => Persist();
    partial void OnDateOfBirthChanged(DateTime? value) { OnPropertyChanged(nameof(AgeText)); OnPropertyChanged(nameof(Turns18Text)); Persist(); }
    partial void OnRelationshipChanged(string value) => Persist();

    private void Persist()
    {
        if (string.IsNullOrWhiteSpace(Name)) return;
        _model.Name = Name.Trim();
        _model.DateOfBirth = DateOfBirth?.Date;
        _model.Relationship = string.IsNullOrWhiteSpace(Relationship) ? null : Relationship.Trim();
        _ = _repo.UpdateAsync(_model);
    }
}

public sealed partial class HouseholdViewModel : ObservableObject
{
    private readonly IHouseholdRepository _repo;

    public HouseholdViewModel(IHouseholdRepository repo) => _repo = repo;

    public static IReadOnlyList<string> RelationshipChoices { get; } = ["Self", "Spouse", "Partner", "Child", "Parent", "Other"];

    public ObservableCollection<HouseholdMemberItem> Members { get; } = new();
    [ObservableProperty] private HouseholdMemberItem? _selected;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private DateTime? _newDateOfBirth;
    [ObservableProperty] private string _newRelationship = "Child";
    [ObservableProperty] private string _status = "";
    public bool Changed { get; private set; }

    public async Task LoadAsync()
    {
        var selectedId = Selected?.Id;
        Members.Clear();
        foreach (var m in await _repo.ListAsync())
            Members.Add(new HouseholdMemberItem(m, await _repo.CountAccountsOfMemberAsync(m.Id), _repo));
        Selected = Members.FirstOrDefault(m => m.Id == selectedId);
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var name = NewName.Trim();
        if (name.Length == 0) { Status = "Enter the member's name."; return; }
        if (Members.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))) { Status = "That name is already in the household."; return; }
        await _repo.InsertAsync(new HouseholdMember { Name = name, DateOfBirth = NewDateOfBirth?.Date, Relationship = NewRelationship });
        NewName = "";
        NewDateOfBirth = null;
        Changed = true;
        Status = $"Added {name}.";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is null) return;
        var m = Selected;
        await _repo.DeleteAsync(m.Id);
        Changed = true;
        Status = m.AccountCount > 0 ? $"Removed {m.Name}; {m.AccountCount} account(s) no longer have an owner." : $"Removed {m.Name}.";
        await LoadAsync();
    }

    [RelayCommand] private Task MoveUpAsync() => MoveAsync(-1);
    [RelayCommand] private Task MoveDownAsync() => MoveAsync(1);

    private async Task MoveAsync(int delta)
    {
        if (Selected is null) return;
        var index = Members.IndexOf(Selected);
        var target = index + delta;
        if (target < 0 || target >= Members.Count) return;
        Members.Move(index, target);
        await _repo.ReorderAsync(Members.Select(m => m.Id).ToList());
        Changed = true;
    }

    public void MarkChanged() => Changed = true;
}
