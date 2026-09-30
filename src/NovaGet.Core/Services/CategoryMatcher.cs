using NovaGet.Core.Models;

namespace NovaGet.Core.Services;

/// <summary>Picks a category from a file's extension using each category's patterns (e.g. <c>zip r0* 7z</c>).</summary>
public sealed class CategoryMatcher
{
    private readonly List<(Category Category, string[] Patterns)> _rules;
    private readonly Category _general;

    public CategoryMatcher(IEnumerable<Category> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var list = categories.ToList();
        _general = list.Find(c => c.Id == Category.GeneralId)
            ?? new Category { Id = Category.GeneralId, Name = "General", IsBuiltIn = true };
        _rules = [.. list
            .Where(c => c.Id != Category.GeneralId)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Id)
            .Select(c => (c, SplitPatterns(c.Extensions)))];
    }

    public Category General => _general;

    /// <summary>The first category (in display order) whose patterns match the extension; General otherwise.</summary>
    public Category Match(string? fileName)
    {
        var extension = ExtensionOf(fileName);
        if (extension.Length == 0)
        {
            return _general;
        }

        foreach (var (category, patterns) in _rules)
        {
            if (patterns.Any(p => ExtensionMatches(p, extension)))
            {
                return category;
            }
        }

        return _general;
    }

    /// <summary>Lower-case extension without the dot ("zip"), or empty.</summary>
    public static string ExtensionOf(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var extension = Path.GetExtension(fileName.Trim());
        return extension.Length > 1 ? extension[1..].ToLowerInvariant() : string.Empty;
    }

    public static string[] SplitPatterns(string? patterns) =>
        string.IsNullOrWhiteSpace(patterns)
            ? []
            : patterns.Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.TrimStart('*', '.').ToLowerInvariant())
                .Where(p => p.Length > 0)
                .ToArray();

    /// <summary>Case-insensitive match with <c>*</c> (any run) and <c>?</c> (one character) wildcards.</summary>
    public static bool ExtensionMatches(string pattern, string extension) =>
        WildcardMatch(pattern.AsSpan(), extension.AsSpan());

    internal static bool WildcardMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
