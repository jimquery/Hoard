using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Sync;

namespace Hoard.Desktop.ViewModels;

public sealed partial class AddInstitutionViewModel : ObservableObject
{
    private readonly InstitutionService _service;

    public AddInstitutionViewModel(InstitutionService service) => _service = service;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _setupToken = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _succeeded;

    public const string SimpleFinBridgeUrl = "https://bridge.simplefin.org/";

    [RelayCommand]
    private async Task AddAsync()
    {
        if (string.IsNullOrWhiteSpace(Name)) { Status = "Enter a name for this connection."; return; }
        if (string.IsNullOrWhiteSpace(SetupToken)) { Status = "Paste the setup token from SimpleFIN Bridge."; return; }
        IsBusy = true;
        Status = "Claiming setup token...";
        try
        {
            var inst = await _service.AddSimpleFinAsync(Name, SetupToken);
            SetupToken = "";
            Status = $"Added \"{inst.Name}\". Run a sync to download accounts.";
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
}
