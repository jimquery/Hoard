using CommunityToolkit.Mvvm.ComponentModel;
using Hoard.Core.Data;
using Hoard.Core.Domain;

namespace Hoard.Desktop.ViewModels;

/// <summary>Row model for the transactions grid. Category edits are routed back to <see cref="MainViewModel"/>.</summary>
public sealed partial class TransactionItem : ObservableObject
{
    private readonly Func<TransactionItem, long?, Task> _onCategoryChanged;
    private readonly Func<TransactionItem, string?, Task> _onNotesChanged;
    private bool _suppress;

    public TransactionItem(TransactionRow row, Func<TransactionItem, long?, Task> onCategoryChanged, Func<TransactionItem, string?, Task> onNotesChanged)
    {
        _onCategoryChanged = onCategoryChanged;
        _onNotesChanged = onNotesChanged;
        Id = row.Id;
        AccountId = row.AccountId;
        AccountName = row.AccountName;
        InstitutionName = row.InstitutionName;
        // Posted dates are stored as UTC midnight of the bank's posting date; show the date as-is, never shifted to local time.
        PostedDate = row.PostedAtUtc.Date;
        Amount = row.Amount;
        Description = row.Description;
        Payee = row.Payee;
        Memo = row.Memo;
        IsPending = row.IsPending;
        _categoryId = row.CategoryId;
        _categorySource = row.CategorySource;
        _categoryPath = row.CategoryPath;
        _notes = row.Notes;
    }

    public long Id { get; }
    public long AccountId { get; }
    public string AccountName { get; }
    public string InstitutionName { get; }
    public DateTime PostedDate { get; }
    public decimal Amount { get; }
    public string Description { get; }
    public string? Payee { get; }
    public string? Memo { get; }
    public bool IsPending { get; }

    [ObservableProperty] private long? _categoryId;
    [ObservableProperty] private CategorySource _categorySource;
    [ObservableProperty] private string _categoryPath;
    [ObservableProperty] private string? _notes;

    public string SourceGlyph => CategorySource switch
    {
        CategorySource.Manual => "✎",
        CategorySource.Imported => "⇲",
        CategorySource.Rule => "⚙",
        _ => "",
    };

    public string SourceTooltip => CategorySource switch
    {
        CategorySource.Manual => "Set by you; rules will not change it.",
        CategorySource.Imported => "Imported with the transaction; rules will not change it.",
        CategorySource.Rule => "Set automatically by a rule.",
        _ => "Uncategorized.",
    };

    /// <summary>Combo-box binding: 0 means uncategorized.</summary>
    public long CategoryIdOrZero
    {
        get => CategoryId ?? CategoryOption.UncategorizedId;
        set
        {
            var next = value == CategoryOption.UncategorizedId ? null : (long?)value;
            if (next == CategoryId) return;
            CategoryId = next;
            OnPropertyChanged();
            if (!_suppress) _ = _onCategoryChanged(this, next);
        }
    }

    partial void OnCategorySourceChanged(CategorySource value)
    {
        OnPropertyChanged(nameof(SourceGlyph));
        OnPropertyChanged(nameof(SourceTooltip));
    }

    partial void OnNotesChanged(string? value)
    {
        if (!_suppress) _ = _onNotesChanged(this, value);
    }

    /// <summary>Update state from the view model without triggering persistence callbacks.</summary>
    public void Apply(long? categoryId, CategorySource source, string categoryPath)
    {
        _suppress = true;
        try
        {
            CategoryId = categoryId;
            CategorySource = source;
            CategoryPath = categoryPath;
            OnPropertyChanged(nameof(CategoryIdOrZero));
        }
        finally { _suppress = false; }
    }
}
