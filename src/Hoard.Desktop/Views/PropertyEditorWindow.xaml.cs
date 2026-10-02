using System.Windows;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class PropertyEditorWindow : Wpf.Ui.Controls.FluentWindow
{
    public PropertyEditorWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PropertyEditorViewModel vm)
                Bar.Title = Title = vm.IsNew ? "Add property" : "Edit property";
        };
    }

    private PropertyEditorViewModel Editor => (PropertyEditorViewModel)DataContext;

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (Owner is not PropertiesWindow { DataContext: PropertiesViewModel list })
        {
            DialogResult = true;
            Close();
            return;
        }

        var error = await list.SaveAsync(Editor);
        if (error is not null)
        {
            Editor.Status = error;
            return;
        }
        DialogResult = true;
        Close();
    }
}
