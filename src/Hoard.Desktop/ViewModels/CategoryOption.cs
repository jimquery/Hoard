using Hoard.Core.Domain;

namespace Hoard.Desktop.ViewModels;

/// <summary>A flattened, display-ready category for combo boxes: "Food &amp; Dining: Groceries".</summary>
public sealed class CategoryOption
{
    public const long UncategorizedId = 0;
    public const long AllId = -1;
    public const long UncategorizedFilterId = -2;

    public long Id { get; init; }
    public long? ParentId { get; init; }
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public CategoryKind Kind { get; init; }
    public bool IsParent => ParentId is null && Id > 0;

    public override string ToString() => Path;

    public static CategoryOption Uncategorized => new() { Id = UncategorizedId, Path = "(uncategorized)" };
    public static CategoryOption All => new() { Id = AllId, Path = "All categories" };
    public static CategoryOption UncategorizedFilter => new() { Id = UncategorizedFilterId, Path = "Uncategorized only" };

    /// <summary>Parents in sort order, each followed by its children.</summary>
    public static List<CategoryOption> Flatten(IEnumerable<TransactionCategory> categories)
    {
        var all = categories.ToList();
        var result = new List<CategoryOption>();
        foreach (var parent in all.Where(c => c.ParentId is null).OrderBy(c => c.SortOrder).ThenBy(c => c.Name))
        {
            result.Add(new CategoryOption { Id = parent.Id, ParentId = null, Name = parent.Name, Path = parent.Name, Kind = parent.Kind });
            foreach (var child in all.Where(c => c.ParentId == parent.Id).OrderBy(c => c.SortOrder).ThenBy(c => c.Name))
                result.Add(new CategoryOption { Id = child.Id, ParentId = parent.Id, Name = child.Name, Path = $"{parent.Name}: {child.Name}", Kind = child.Kind });
        }
        return result;
    }
}
