using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Hoard.Desktop.Controls;

/// <summary>
/// A type-to-filter picker: a text box whose drop-down list narrows to items containing what was typed (anywhere in
/// the text). Bind either <see cref="SelectedItem"/> or <see cref="SelectedValue"/> + <see cref="SelectedValuePath"/>.
/// Built from a TextBox and a Popup rather than an editable ComboBox, which keeps rewriting its own text.
/// </summary>
public partial class FilterPicker : UserControl
{
    public FilterPicker()
    {
        InitializeComponent();
        LostKeyboardFocus += OnLostKeyboardFocus;
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(FilterPicker), new PropertyMetadata(null, OnItemsSourceChanged));
    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(object), typeof(FilterPicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedItemChanged));
    public static readonly DependencyProperty SelectedValueProperty = DependencyProperty.Register(
        nameof(SelectedValue), typeof(object), typeof(FilterPicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedValueChanged));
    public static readonly DependencyProperty SelectedValuePathProperty = DependencyProperty.Register(
        nameof(SelectedValuePath), typeof(string), typeof(FilterPicker), new PropertyMetadata(""));
    public static readonly DependencyProperty DisplayMemberPathProperty = DependencyProperty.Register(
        nameof(DisplayMemberPath), typeof(string), typeof(FilterPicker), new PropertyMetadata(""));

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public object? SelectedValue { get => GetValue(SelectedValueProperty); set => SetValue(SelectedValueProperty, value); }
    public string SelectedValuePath { get => (string)GetValue(SelectedValuePathProperty); set => SetValue(SelectedValuePathProperty, value); }
    public string DisplayMemberPath { get => (string)GetValue(DisplayMemberPathProperty); set => SetValue(DisplayMemberPathProperty, value); }

    private bool _syncing;      // true while the control itself writes SelectedItem/SelectedValue/Text
    private string _filter = "";
    private DateTime _closedAt;  // the arrow click that closes the popup arrives just after StaysOpen=False closed it

    // ----- dependency property plumbing -----

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p = (FilterPicker)d;
        if (e.OldValue is INotifyCollectionChanged oldN) oldN.CollectionChanged -= p.OnSourceChanged;
        if (e.NewValue is INotifyCollectionChanged newN) newN.CollectionChanged += p.OnSourceChanged;
        p.RefreshList();
        p.ResolveSelectedFromValue();
        p.ShowSelectedText();
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshList();
        ResolveSelectedFromValue();
        ShowSelectedText();
    }

    private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p = (FilterPicker)d;
        if (p._syncing) return;
        p._syncing = true;
        try { p.SelectedValue = p.ValueOf(e.NewValue); }
        finally { p._syncing = false; }
        p.ShowSelectedText();
    }

    private static void OnSelectedValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p = (FilterPicker)d;
        if (p._syncing) return;
        p.ResolveSelectedFromValue();
        p.ShowSelectedText();
    }

    /// <summary>SelectedValue was set from outside: find the matching item.</summary>
    private void ResolveSelectedFromValue()
    {
        if (string.IsNullOrEmpty(SelectedValuePath) || ItemsSource is null) return;
        var value = SelectedValue;
        if (value is null) return;
        var match = ItemsSource.Cast<object>().FirstOrDefault(o => Equals(ValueOf(o), value));
        if (match is null || ReferenceEquals(match, SelectedItem)) return;
        _syncing = true;
        try { SelectedItem = match; }
        finally { _syncing = false; }
    }

    private object? ValueOf(object? item) =>
        item is null ? null : string.IsNullOrEmpty(SelectedValuePath) ? item : item.GetType().GetProperty(SelectedValuePath)?.GetValue(item);

    private string TextOf(object? item) =>
        item is null ? "" : string.IsNullOrEmpty(DisplayMemberPath) ? item.ToString() ?? "" : item.GetType().GetProperty(DisplayMemberPath)?.GetValue(item)?.ToString() ?? item.ToString() ?? "";

    private void Commit(object? item)
    {
        _syncing = true;
        try
        {
            SelectedItem = item;
            SelectedValue = ValueOf(item);
        }
        finally { _syncing = false; }
        Pop.IsOpen = false;
        ShowSelectedText();
    }

    private void ShowSelectedText()
    {
        var text = TextOf(SelectedItem);
        if (Box.Text == text) return;
        _syncing = true;
        try { Box.Text = text; Box.CaretIndex = text.Length; }
        finally { _syncing = false; }
    }

    // ----- list / filtering -----

    private void RefreshList()
    {
        var items = ItemsSource?.Cast<object>() ?? Enumerable.Empty<object>();
        var f = _filter;
        var filtered = f.Length == 0 ? items.ToList() : items.Where(o => TextOf(o).Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        List.ItemsSource = filtered;
        List.DisplayMemberPath = DisplayMemberPath;
        // Keep the current selection highlighted when it is in the list; otherwise highlight the first match.
        List.SelectedItem = filtered.FirstOrDefault(o => ReferenceEquals(o, SelectedItem)) ?? (f.Length > 0 ? filtered.FirstOrDefault() : null);
        if (List.SelectedItem is not null) List.ScrollIntoView(List.SelectedItem);
    }

    private void Open()
    {
        RefreshList();
        if (!Pop.IsOpen) Pop.IsOpen = true;
    }

    // ----- text box events -----

    private void OnBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Select everything so typing starts a fresh filter instead of appending to the current category's name.
        Box.SelectAll();
    }

    private void OnBoxMouseDown(object sender, MouseButtonEventArgs e)
    {
        // A mouse click would place the caret and drop the select-all; take the first click to focus + select instead.
        if (Box.IsKeyboardFocusWithin) return;
        Box.Focus();
        Box.SelectAll();
        e.Handled = true;
    }

    private void OnBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || !Box.IsKeyboardFocusWithin) return;
        _filter = Box.Text.Trim();
        Open();
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (!Pop.IsOpen) { _filter = ""; Open(); }
                else Move(+1);
                e.Handled = true;
                break;
            case Key.Up:
                if (Pop.IsOpen) { Move(-1); e.Handled = true; }
                break;
            case Key.Enter:
                if (Pop.IsOpen && List.SelectedItem is { } chosen) { Commit(chosen); e.Handled = true; }
                break;
            case Key.Escape:
                if (Pop.IsOpen) { Pop.IsOpen = false; ShowSelectedText(); e.Handled = true; }
                break;
            case Key.Tab:
                if (Pop.IsOpen && _filter.Length > 0 && List.SelectedItem is { } first) Commit(first); // tab accepts the highlighted match
                break;
        }
    }

    private void Move(int delta)
    {
        if (List.Items.Count == 0) return;
        var index = Math.Clamp(List.SelectedIndex + delta, 0, List.Items.Count - 1);
        List.SelectedIndex = index;
        List.ScrollIntoView(List.Items[index]);
    }

    private void OnArrowClick(object sender, RoutedEventArgs e)
    {
        if (Pop.IsOpen || (DateTime.UtcNow - _closedAt).TotalMilliseconds < 300) { Pop.IsOpen = false; Arrow.IsChecked = false; return; }
        _filter = "";
        Box.Focus();
        Open();
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Commit the row under the mouse (selection already moved there on mouse-down).
        var item = ItemsControl.ContainerFromElement(List, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item is not null) { Commit(item.DataContext); e.Handled = true; Box.Focus(); }
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        _closedAt = DateTime.UtcNow;
        Arrow.IsChecked = false;
        _filter = "";
        ShowSelectedText();
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (IsKeyboardFocusWithin) return;
        if (Pop.IsOpen) Pop.IsOpen = false;
        _filter = "";
        ShowSelectedText();
    }

    /// <summary>Lets the picker be used where a ComboBox's Text would be read.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string Text => Box.Text;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Box.IsKeyboardFocusWithin) Box.Focus();
    }
}
