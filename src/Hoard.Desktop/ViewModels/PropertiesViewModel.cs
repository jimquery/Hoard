using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Domain;
using Hoard.Core.Providers.RealEstate;

namespace Hoard.Desktop.ViewModels;

public sealed class PropertyItem
{
    public PropertyItem(Property p) => Model = p;
    public Property Model { get; }
    public long Id => Model.Id;
    public string Nickname => Model.Nickname;
    public string Address => Model.FormattedAddress;
    public string Details => string.Join(" · ", new[]
    {
        Model.PropertyType ?? "",
        Model.Bedrooms is { } b ? $"{b:0.#} bd" : "",
        Model.Bathrooms is { } ba ? $"{ba:0.#} ba" : "",
        Model.SquareFootage is { } s ? $"{s:N0} sq ft" : "",
    }.Where(x => x.Length > 0));
    public decimal Value => Model.CurrentValue;
    public string Range => Model.ValuationSource == ValuationSource.OnlineAvm && Model.LastValueLow is { } lo && Model.LastValueHigh is { } hi
        ? $"{lo:C0} – {hi:C0}" : "";
    public string Source => Model.ValuationSource == ValuationSource.Manual ? "Manual" : "RentCast";
    public string ValuedAt => Model.LastValuedAtUtc is { } d ? d.ToLocalTime().ToString("MMM d, yyyy") : (Model.ValuationSource == ValuationSource.OnlineAvm ? "not yet" : "");
    public string? Error => Model.LastError;
    public string Gain => Model.PurchasePrice is { } pp && pp > 0 ? $"{(Model.CurrentValue - pp):+#,##0;-#,##0;0} ({(Model.CurrentValue - pp) / pp:+0%;-0%;0%})" : "";
}

/// <summary>Add/edit form for one property.</summary>
public sealed partial class PropertyEditorViewModel : ObservableObject
{
    public PropertyEditorViewModel(Property? existing)
    {
        var p = existing ?? new Property();
        IsNew = existing is null;
        _nickname = p.Nickname; _addressLine1 = p.AddressLine1; _addressLine2 = p.AddressLine2 ?? ""; _city = p.City; _state = p.State; _zip = p.Zip;
        _propertyType = p.PropertyType ?? ""; _bedrooms = p.Bedrooms?.ToString(CultureInfo.CurrentCulture) ?? ""; _bathrooms = p.Bathrooms?.ToString(CultureInfo.CurrentCulture) ?? "";
        _squareFootage = p.SquareFootage?.ToString(CultureInfo.CurrentCulture) ?? ""; _purchasePrice = p.PurchasePrice?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        _purchaseDate = p.PurchaseDate; _useOnlineValue = p.ValuationSource == ValuationSource.OnlineAvm;
        _manualValue = p.ManualValue?.ToString("0.##", CultureInfo.CurrentCulture) ?? ""; _refreshDays = p.RefreshDays;
        Model = p;
    }

    public bool IsNew { get; }
    public Property Model { get; }
    public static IReadOnlyList<string> PropertyTypes { get; } = ["", "Single Family", "Condo", "Townhouse", "Manufactured", "Multi-Family", "Apartment", "Land"];

    [ObservableProperty] private string _nickname;
    [ObservableProperty] private string _addressLine1;
    [ObservableProperty] private string _addressLine2;
    [ObservableProperty] private string _city;
    [ObservableProperty] private string _state;
    [ObservableProperty] private string _zip;
    [ObservableProperty] private string _propertyType;
    [ObservableProperty] private string _bedrooms;
    [ObservableProperty] private string _bathrooms;
    [ObservableProperty] private string _squareFootage;
    [ObservableProperty] private string _purchasePrice;
    [ObservableProperty] private DateTime? _purchaseDate;
    [ObservableProperty] private bool _useOnlineValue;
    [ObservableProperty] private string _manualValue;
    [ObservableProperty] private double _refreshDays; // double: bound to a NumberBox
    [ObservableProperty] private string _status = "";

    /// <summary>Copies the form into the model; returns an error message or null.</summary>
    public string? Apply()
    {
        Model.Nickname = Nickname;
        Model.AddressLine1 = AddressLine1;
        Model.AddressLine2 = string.IsNullOrWhiteSpace(AddressLine2) ? null : AddressLine2.Trim();
        Model.City = City; Model.State = State; Model.Zip = Zip;
        Model.PropertyType = string.IsNullOrWhiteSpace(PropertyType) ? null : PropertyType;
        Model.Bedrooms = ParseDecimal(Bedrooms);
        Model.Bathrooms = ParseDecimal(Bathrooms);
        Model.SquareFootage = int.TryParse(SquareFootage, NumberStyles.Number, CultureInfo.CurrentCulture, out var sf) ? sf : null;
        Model.PurchasePrice = ParseDecimal(PurchasePrice);
        Model.PurchaseDate = PurchaseDate?.Date;
        Model.ValuationSource = UseOnlineValue ? ValuationSource.OnlineAvm : ValuationSource.Manual;
        Model.ManualValue = ParseDecimal(ManualValue);
        Model.RefreshDays = Math.Clamp((int)Math.Round(RefreshDays), 1, 365);
        if (string.IsNullOrWhiteSpace(AddressLine1) || string.IsNullOrWhiteSpace(City) || string.IsNullOrWhiteSpace(State) || string.IsNullOrWhiteSpace(Zip))
            return "Street, city, state and ZIP are required.";
        if (!UseOnlineValue && Model.ManualValue is null && Model.PurchasePrice is null)
            return "Enter a value (or a purchase price) for a manually valued property.";
        return null;
    }

    private static decimal? ParseDecimal(string text) =>
        decimal.TryParse(text?.Replace("$", "").Replace(",", ""), NumberStyles.Number, CultureInfo.CurrentCulture, out var d) ? d : null;
}

public sealed partial class PropertiesViewModel : ObservableObject
{
    private readonly PropertyService _service;

    public PropertiesViewModel(PropertyService service) => _service = service;

    public PropertyService Service => _service;
    public ObservableCollection<PropertyItem> Properties { get; } = new();
    [ObservableProperty] private PropertyItem? _selected;
    [ObservableProperty] private bool _hasApiKey;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private decimal _total;
    public bool Changed { get; private set; }

    public async Task LoadAsync()
    {
        var selectedId = Selected?.Id;
        Properties.Clear();
        foreach (var p in await _service.ListAsync()) Properties.Add(new PropertyItem(p));
        Selected = Properties.FirstOrDefault(p => p.Id == selectedId);
        Total = Properties.Sum(p => p.Value);
        HasApiKey = await _service.HasApiKeyAsync();
    }

    public async Task SaveApiKeyAsync(string? key)
    {
        await _service.SetApiKeyAsync(key);
        HasApiKey = !string.IsNullOrWhiteSpace(key);
        Status = HasApiKey ? "RentCast API key saved (encrypted)." : "RentCast API key cleared; online valuations are off.";
        Changed = true;
    }

    public async Task<string?> SaveAsync(PropertyEditorViewModel editor)
    {
        var error = editor.Apply();
        if (error is not null) return error;
        try
        {
            if (editor.IsNew) await _service.AddAsync(editor.Model);
            else await _service.UpdateAsync(editor.Model);
            Changed = true;
            await LoadAsync();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Selected is null) return;
        var name = Selected.Nickname;
        await _service.RemoveAsync(Selected.Id);
        Changed = true;
        Status = $"Removed {name} and its account.";
        await LoadAsync();
    }

    [RelayCommand]
    private Task RefreshDueAsync() => RefreshAsync(force: false);

    [RelayCommand]
    private Task RefreshAllAsync() => RefreshAsync(force: true);

    private async Task RefreshAsync(bool force)
    {
        IsBusy = true;
        try
        {
            var result = await _service.RefreshAsync(force);
            Changed = true;
            Status = result.Status == SyncStatus.Failed
                ? "Refresh failed: " + result.Error
                : $"Values refreshed for {result.AccountsSeen} propert{(result.AccountsSeen == 1 ? "y" : "ies")}." + (result.Error is null ? "" : " " + result.Error.Replace("\n", " "));
            await LoadAsync();
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }
}
