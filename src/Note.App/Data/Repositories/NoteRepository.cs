using Microsoft.Data.Sqlite;
using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Models.Enums;

namespace NoteApp.Data.Repositories;

/// <summary>便签数据访问实现（SQLite 参数化读写，不含业务逻辑）。</summary>
public class NoteRepository : INoteRepository
{
    private const string NoteColumns =
        "Id, Title, Content, Color, PositionX, PositionY, Width, Height, Opacity, " +
        "IsPinned, IsLocked, IsArchived, IsCollapsed, FontSize, ZIndex, CreatedAt, UpdatedAt, DeletedAt, IsMinimal, " +
        "IsDockEnabled, DockEdge, IsDogEar";

    /// <summary>
    /// 插入一条便签的完整语句，新增与导入共用。
    /// 导入需要**原样保留** Id 与 CreatedAt/DeletedAt，所以必须显式写这些列
    /// （<see cref="Create"/> 走同一条语句，只是把 Id/时间戳换成新生成的）。
    /// </summary>
    private const string InsertSql =
        "INSERT INTO Notes (Id, Title, Content, Color, PositionX, PositionY, Width, Height, Opacity, " +
        "IsPinned, IsLocked, IsArchived, IsCollapsed, FontSize, ZIndex, CreatedAt, UpdatedAt, DeletedAt, IsMinimal, " +
        "IsDockEnabled, DockEdge, IsDogEar) " +
        "VALUES ($id, $title, $content, $color, $positionX, $positionY, $width, $height, $opacity, " +
        "$isPinned, $isLocked, $isArchived, $isCollapsed, $fontSize, $zIndex, $createdAt, $updatedAt, $deletedAt, $isMinimal, " +
        "$isDockEnabled, $dockEdge, $isDogEar);";

    private readonly AppDbContext _dbContext;

    public NoteRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Note? GetById(Guid id)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {NoteColumns} FROM Notes WHERE Id = $id AND DeletedAt IS NULL;";
        command.Parameters.AddWithValue("$id", GuidHelper.ToStorage(id));

        using var reader = command.ExecuteReader();
        return reader.Read() ? MapNote(reader) : null;
    }

    public List<Note> GetAll(bool includeArchived = false)
    {
        var sql = $"SELECT {NoteColumns} FROM Notes WHERE DeletedAt IS NULL";
        if (!includeArchived)
        {
            sql += " AND IsArchived = 0";
        }
        sql += " ORDER BY IsPinned DESC, UpdatedAt DESC;";

        return QueryList(sql);
    }

    public List<Note> GetAllIncludingDeleted()
    {
        var sql = $"SELECT {NoteColumns} FROM Notes ORDER BY IsPinned DESC, UpdatedAt DESC;";

        return QueryList(sql);
    }

    public List<Note> GetByTag(int tagId)
    {
        var sql = $"SELECT {NoteColumns} FROM Notes n INNER JOIN NoteTags nt ON nt.NoteId = n.Id " +
                  "WHERE nt.TagId = $tagId AND n.DeletedAt IS NULL ORDER BY n.IsPinned DESC, n.UpdatedAt DESC;";

        return QueryList(sql, ("$tagId", tagId));
    }

    public List<Note> Search(string keyword)
    {
        var sql = $"SELECT {NoteColumns} FROM Notes " +
                  "WHERE DeletedAt IS NULL AND (Title LIKE $kw OR Content LIKE $kw) " +
                  "ORDER BY IsPinned DESC, UpdatedAt DESC;";

        return QueryList(sql, ("$kw", $"%{keyword}%"));
    }

    public List<Note> GetDeleted()
    {
        var sql = $"SELECT {NoteColumns} FROM Notes WHERE DeletedAt IS NOT NULL ORDER BY DeletedAt DESC;";

        return QueryList(sql);
    }

    public Note Create(Note note)
    {
        if (note.Id == Guid.Empty)
        {
            note.Id = Guid.NewGuid();
        }

        var now = DateTime.Now;
        note.CreatedAt = now;
        note.UpdatedAt = now;
        note.DeletedAt = null;

        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = InsertSql;
        AddCommonParameters(command, note);
        command.Parameters.AddWithValue("$createdAt", DateTimeHelper.ToStorage(note.CreatedAt));
        command.Parameters.AddWithValue("$deletedAt", DBNull.Value);
        command.ExecuteNonQuery();

        return note;
    }

    public Note Update(Note note)
    {
        note.UpdatedAt = DateTime.Now;

        const string sql =
            "UPDATE Notes SET " +
            "Title = $title, Content = $content, Color = $color, " +
            "PositionX = $positionX, PositionY = $positionY, Width = $width, Height = $height, " +
            "Opacity = $opacity, IsPinned = $isPinned, IsLocked = $isLocked, " +
            "IsArchived = $isArchived, IsCollapsed = $isCollapsed, IsMinimal = $isMinimal, FontSize = $fontSize, " +
            "IsDockEnabled = $isDockEnabled, DockEdge = $dockEdge, IsDogEar = $isDogEar, " +
            "ZIndex = $zIndex, UpdatedAt = $updatedAt " +
            "WHERE Id = $id;";

        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddCommonParameters(command, note);
        command.ExecuteNonQuery();

        return note;
    }

    public void SoftDelete(Guid id)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Notes SET DeletedAt = $deletedAt WHERE Id = $id;";
        command.Parameters.AddWithValue("$deletedAt", DateTimeHelper.ToStorage(DateTime.Now));
        command.Parameters.AddWithValue("$id", GuidHelper.ToStorage(id));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 批量导入：单事务、保留原 Id 与时间戳、已存在的跳过。
    /// 之所以要事务：一次导入可能有几百条，中途失败留下「导了一半」的状态
    /// 比整体失败更难收拾。
    /// </summary>
    public (int Inserted, int Skipped) InsertImportedBatch(IReadOnlyList<Note> notes)
    {
        if (notes.Count == 0)
        {
            return (0, 0);
        }

        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var inserted = 0;
        var skipped = 0;

        foreach (var note in notes)
        {
            if (ExistsIn(connection, transaction, note.Id))
            {
                skipped++;
                continue;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = InsertSql;
            AddCommonParameters(command, note);
            command.Parameters.AddWithValue("$createdAt", DateTimeHelper.ToStorage(note.CreatedAt));
            command.Parameters.AddWithValue(
                "$deletedAt",
                note.DeletedAt is null ? DBNull.Value : DateTimeHelper.ToStorage(note.DeletedAt.Value));

            inserted += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return (inserted, skipped);
    }

    private static bool ExistsIn(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM Notes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", GuidHelper.ToStorage(id));
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    public void Restore(Guid id)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Notes SET DeletedAt = NULL WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", GuidHelper.ToStorage(id));
        command.ExecuteNonQuery();
    }

    public void PermanentDelete(Guid id)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Notes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", GuidHelper.ToStorage(id));
        command.ExecuteNonQuery();
    }

    public int GetCount(bool includeDeleted = false)
    {
        var sql = "SELECT COUNT(*) FROM Notes";
        if (!includeDeleted)
        {
            sql += " WHERE DeletedAt IS NULL";
        }
        sql += ";";

        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private List<Note> QueryList(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var notes = new List<Note>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            notes.Add(MapNote(reader));
        }
        return notes;
    }

    /// <summary>绑定 Insert/Update 共用的字段（不含 CreatedAt、DeletedAt）。</summary>
    private static void AddCommonParameters(SqliteCommand command, Note note)
    {
        command.Parameters.AddWithValue("$id", GuidHelper.ToStorage(note.Id));
        command.Parameters.AddWithValue("$title", (object?)note.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$content", note.Content);
        command.Parameters.AddWithValue("$color", NoteColorHelper.ToStorage(note.Color));
        command.Parameters.AddWithValue("$positionX", note.PositionX);
        command.Parameters.AddWithValue("$positionY", note.PositionY);
        command.Parameters.AddWithValue("$width", note.Width);
        command.Parameters.AddWithValue("$height", note.Height);
        command.Parameters.AddWithValue("$opacity", note.Opacity);
        command.Parameters.AddWithValue("$isPinned", note.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$isLocked", note.IsLocked ? 1 : 0);
        command.Parameters.AddWithValue("$isArchived", note.IsArchived ? 1 : 0);
        command.Parameters.AddWithValue("$isCollapsed", note.IsCollapsed ? 1 : 0);
        command.Parameters.AddWithValue("$isMinimal", note.IsMinimal ? 1 : 0);
        command.Parameters.AddWithValue("$isDockEnabled", note.IsDockEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$dockEdge", (int)note.DockEdge);
        command.Parameters.AddWithValue("$isDogEar", note.IsDogEar ? 1 : 0);
        command.Parameters.AddWithValue("$fontSize", (object?)note.FontSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$zIndex", note.ZIndex);
        command.Parameters.AddWithValue("$updatedAt", DateTimeHelper.ToStorage(note.UpdatedAt));
    }

    private static Note MapNote(SqliteDataReader reader)
    {
        return new Note
        {
            Id = Guid.Parse(reader.GetString(0)),
            Title = reader.IsDBNull(1) ? null : reader.GetString(1),
            Content = reader.GetString(2),
            Color = NoteColorHelper.Parse(reader.GetString(3)),
            PositionX = reader.GetInt32(4),
            PositionY = reader.GetInt32(5),
            Width = reader.GetInt32(6),
            Height = reader.GetInt32(7),
            Opacity = reader.GetInt32(8),
            IsPinned = reader.GetInt32(9) != 0,
            IsLocked = reader.GetInt32(10) != 0,
            IsArchived = reader.GetInt32(11) != 0,
            IsCollapsed = reader.GetInt32(12) != 0,
            FontSize = reader.IsDBNull(13) ? null : reader.GetDouble(13),
            ZIndex = reader.GetInt32(14),
            CreatedAt = DateTimeHelper.Parse(reader.GetString(15)),
            UpdatedAt = DateTimeHelper.Parse(reader.GetString(16)),
            DeletedAt = reader.IsDBNull(17) ? null : DateTimeHelper.Parse(reader.GetString(17)),
            IsMinimal = reader.GetInt32(18) != 0,
            IsDockEnabled = reader.GetInt32(19) != 0,
            // 存的是整数，非法值一律回落到 None（宁可"不贴边"，也不要解析异常导致整条便签读不出来）
            DockEdge = Enum.IsDefined(typeof(DockEdge), reader.GetInt32(20))
                ? (DockEdge)reader.GetInt32(20)
                : DockEdge.None,
            IsDogEar = reader.GetInt32(21) != 0,
        };
    }
}
