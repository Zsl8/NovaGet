using Dapper;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Models;

namespace NovaGet.Data.Repositories;

public sealed class CategoryRepository(SqliteDatabase database) : ICategoryRepository
{
    private const string SelectColumns = "SELECT id, name, parentId, extensions, defaultSaveDir, icon, isBuiltIn, sortOrder FROM Category";

    public IReadOnlyList<Category> GetAll()
    {
        using var connection = database.Open();
        return [.. connection.Query<Category>(SelectColumns + " ORDER BY sortOrder, id;")];
    }

    public Category? Get(long id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Category>(SelectColumns + " WHERE id = @id;", new { id });
    }

    public long Insert(Category category)
    {
        ArgumentNullException.ThrowIfNull(category);
        using var connection = database.Open();
        category.Id = connection.ExecuteScalar<long>(
            """
            INSERT INTO Category (name, parentId, extensions, defaultSaveDir, icon, isBuiltIn, sortOrder)
            VALUES (@Name, @ParentId, @Extensions, @DefaultSaveDir, @Icon, 0,
                    COALESCE(NULLIF(@SortOrder, 0), (SELECT COALESCE(MAX(sortOrder), 0) + 1 FROM Category)));
            SELECT last_insert_rowid();
            """,
            category);
        category.IsBuiltIn = false;
        return category.Id;
    }

    public void Update(Category category)
    {
        ArgumentNullException.ThrowIfNull(category);
        using var connection = database.Open();

        // Built-in categories keep their name so the tree and the Options dialog stay consistent.
        connection.Execute(
            """
            UPDATE Category SET
                name = CASE WHEN isBuiltIn = 1 THEN name ELSE @Name END,
                parentId = CASE WHEN isBuiltIn = 1 THEN parentId ELSE @ParentId END,
                extensions = @Extensions, defaultSaveDir = @DefaultSaveDir, icon = @Icon, sortOrder = @SortOrder
            WHERE id = @Id;
            """,
            category);
    }

    public bool Delete(long id)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var builtIn = connection.ExecuteScalar<long?>("SELECT isBuiltIn FROM Category WHERE id = @id;", new { id }, transaction);
        if (builtIn is null or 1)
        {
            return false;
        }

        connection.Execute("UPDATE Download SET categoryId = @general WHERE categoryId = @id;", new { id, general = Category.GeneralId }, transaction);
        connection.Execute("UPDATE Category SET parentId = @general WHERE parentId = @id;", new { id, general = Category.GeneralId }, transaction);
        connection.Execute("DELETE FROM Category WHERE id = @id;", new { id }, transaction);
        transaction.Commit();
        return true;
    }
}
