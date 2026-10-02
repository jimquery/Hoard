using System.Windows;

namespace Hoard.Desktop.Views;

/// <summary>Small single-field prompt used for renames and secrets.</summary>
public partial class InputDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly bool _masked;

    public InputDialog() : this("Hoard", "", "", masked: false) { }

    public InputDialog(string title, string prompt, string initialValue, bool masked)
    {
        InitializeComponent();
        _masked = masked;
        Title = title;
        Bar.Title = title;
        Prompt.Text = prompt;
        if (masked)
        {
            Input.Visibility = Visibility.Collapsed;
            Secret.Visibility = Visibility.Visible;
            Secret.Password = initialValue;
            Loaded += (_, _) => Secret.Focus();
        }
        else
        {
            Input.Text = initialValue;
            Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
        }
    }

    public string Value => _masked ? Secret.Password : Input.Text;

    /// <summary>Returns the entered text, or null if cancelled.</summary>
    public static string? Show(Window owner, string title, string prompt, string initial)
    {
        var dlg = new InputDialog(title, prompt, initial, masked: false) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
