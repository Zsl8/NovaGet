using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Dapper;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Models;

namespace NovaGet.Data.Repositories;

public sealed class QueueRepository(SqliteDatabase database) : IQueueRepository
{
    private const string SelectColumns = "SELECT id, name, isBuiltIn, simultaneousCount, scheduleJson, isSyncQueue, syncIntervalMin FROM Queue";

    private static readonly JsonSerializerOptions s_json = CreateJsonOptions();

    public IReadOnlyList<DownloadQueue> GetAll()
    {
        using var connection = database.Open();
        return [.. connection.Query<QueueRow>(SelectColumns + " ORDER BY id;").Select(ToModel)];
    }

    public DownloadQueue? Get(long id)
    {
        using var connection = database.Open();
        var row = connection.QuerySingleOrDefault<QueueRow>(SelectColumns + " WHERE id = @id;", new { id });
        return row is null ? null : ToModel(row);
    }

    public DownloadQueue? GetByName(string name)
    {
        using var connection = database.Open();
        var row = connection.QuerySingleOrDefault<QueueRow>(SelectColumns + " WHERE name = @name COLLATE NOCASE;", new { name });
        return row is null ? null : ToModel(row);
    }

    public long Insert(DownloadQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        using var connection = database.Open();
        queue.Id = connection.ExecuteScalar<long>(
            """
            INSERT INTO Queue (name, isBuiltIn, simultaneousCount, scheduleJson, isSyncQueue, syncIntervalMin)
            VALUES (@Name, 0, @SimultaneousCount, @ScheduleJson, 0, @SyncIntervalMin);
            SELECT last_insert_rowid();
            """,
            ToParameters(queue));
        queue.IsBuiltIn = false;
        queue.IsSyncQueue = false;
        return queue.Id;
    }

    public void Update(DownloadQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE Queue SET
                name = CASE WHEN isBuiltIn = 1 THEN name ELSE @Name END,
                simultaneousCount = @SimultaneousCount, scheduleJson = @ScheduleJson, syncIntervalMin = @SyncIntervalMin
            WHERE id = @Id;
            """,
            ToParameters(queue));
    }

    public bool Delete(long id)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var builtIn = connection.ExecuteScalar<long?>("SELECT isBuiltIn FROM Queue WHERE id = @id;", new { id }, transaction);
        if (builtIn is null or 1)
        {
            return false;
        }

        connection.Execute("UPDATE Download SET queueId = NULL, queuePosition = 0 WHERE queueId = @id;", new { id }, transaction);
        connection.Execute("DELETE FROM Queue WHERE id = @id;", new { id }, transaction);
        transaction.Commit();
        return true;
    }

    private static object ToParameters(DownloadQueue q) => new
    {
        q.Id,
        q.Name,
        SimultaneousCount = Math.Max(1, q.SimultaneousCount),
        ScheduleJson = JsonSerializer.Serialize(q.Schedule ?? new QueueSchedule(), s_json),
        SyncIntervalMin = Math.Max(1, q.SyncIntervalMinutes),
    };

    private static DownloadQueue ToModel(QueueRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        IsBuiltIn = row.IsBuiltIn,
        SimultaneousCount = (int)row.SimultaneousCount,
        Schedule = ParseSchedule(row.ScheduleJson),
        IsSyncQueue = row.IsSyncQueue,
        SyncIntervalMinutes = (int)row.SyncIntervalMin,
    };

    private static QueueSchedule ParseSchedule(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new QueueSchedule();
        }

        try
        {
            return JsonSerializer.Deserialize<QueueSchedule>(json, s_json) ?? new QueueSchedule();
        }
        catch (JsonException)
        {
            return new QueueSchedule();
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        options.MakeReadOnly();
        return options;
    }

    private sealed class QueueRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool IsBuiltIn { get; set; }

        public long SimultaneousCount { get; set; }

        public string? ScheduleJson { get; set; }

        public bool IsSyncQueue { get; set; }

        public long SyncIntervalMin { get; set; }
    }
}
