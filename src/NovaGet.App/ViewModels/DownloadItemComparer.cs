using System.Collections;

namespace NovaGet.App.ViewModels;

/// <summary>Fast typed comparer for the list's sort keys (ListCollectionView.CustomSort).</summary>
public sealed class DownloadItemComparer(string key, bool descending) : IComparer
{
    public const string OrderOfAddition = "Id";

    public static readonly string[] Keys =
        [OrderOfAddition, "FileName", "Size", "StatusRank", "TimeLeftSeconds", "Rate", "LastTry", "Description", "SaveTo", "Referrer", "QueuePosition", "AddedAt", "Connections"];

    public string Key { get; } = key;

    public bool Descending { get; } = descending;

    public int Compare(object? x, object? y)
    {
        if (x is not DownloadItemViewModel a || y is not DownloadItemViewModel b)
        {
            return 0;
        }

        var result = Key switch
        {
            "FileName" => string.Compare(a.FileName, b.FileName, StringComparison.CurrentCultureIgnoreCase),
            "Size" => a.Size.CompareTo(b.Size),
            "StatusRank" => a.StatusRank.CompareTo(b.StatusRank),
            "TimeLeftSeconds" => a.TimeLeftSeconds.CompareTo(b.TimeLeftSeconds),
            "Rate" => a.Rate.CompareTo(b.Rate),
            "LastTry" => Nullable.Compare(a.LastTry, b.LastTry),
            "Description" => string.Compare(a.Description, b.Description, StringComparison.CurrentCultureIgnoreCase),
            "SaveTo" => string.Compare(a.SaveTo, b.SaveTo, StringComparison.CurrentCultureIgnoreCase),
            "Referrer" => string.Compare(a.Referrer, b.Referrer, StringComparison.OrdinalIgnoreCase),
            "QueuePosition" => (a.QueuePosition ?? int.MaxValue).CompareTo(b.QueuePosition ?? int.MaxValue),
            "AddedAt" => a.AddedAt.CompareTo(b.AddedAt),
            "Connections" => a.Connections.CompareTo(b.Connections),
            _ => 0,
        };

        if (result == 0)
        {
            result = a.Id.CompareTo(b.Id); // stable
        }

        return Descending ? -result : result;
    }
}
