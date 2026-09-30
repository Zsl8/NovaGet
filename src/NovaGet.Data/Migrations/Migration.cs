namespace NovaGet.Data.Migrations;

/// <summary>One schema step. Versions start at 1 and must be contiguous.</summary>
public sealed record Migration(int Version, string Name, string Sql);
