using NovaGet.Core.Abstractions;

namespace NovaGet.Core.Services.Queues;

public enum QueueNameProblem
{
    None,
    Empty,
    TooLong,
    InvalidCharacters,
    Exists,
}

/// <summary>Queue names are passed on command lines (<c>/startqueue "name"</c>), so they can't contain quotes.</summary>
public static class QueueNames
{
    public const int MaxLength = 100;

    public static QueueNameProblem Check(string? name, IQueueRepository queues, long? renamingId = null)
    {
        ArgumentNullException.ThrowIfNull(queues);
        name = name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return QueueNameProblem.Empty;
        }

        if (name.Length > MaxLength)
        {
            return QueueNameProblem.TooLong;
        }

        if (name.Any(c => c == '"' || char.IsControl(c)))
        {
            return QueueNameProblem.InvalidCharacters;
        }

        var existing = queues.GetByName(name);
        return existing is not null && existing.Id != renamingId ? QueueNameProblem.Exists : QueueNameProblem.None;
    }
}
