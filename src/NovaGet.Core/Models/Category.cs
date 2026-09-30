namespace NovaGet.Core.Models;

/// <summary>Download category (table <c>Category</c>). Built-ins cannot be deleted.</summary>
public sealed class Category
{
    public const long GeneralId = 1;

    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public long? ParentId { get; set; }

    /// <summary>Space-separated extension patterns, e.g. <c>zip rar r0* 7z</c>.</summary>
    public string Extensions { get; set; } = string.Empty;

    /// <summary>Null means the built-in default (<c>Downloads\&lt;Category&gt;</c>, or Downloads for General).</summary>
    public string? DefaultSaveDir { get; set; }

    public string? Icon { get; set; }

    public bool IsBuiltIn { get; set; }

    public int SortOrder { get; set; }
}
