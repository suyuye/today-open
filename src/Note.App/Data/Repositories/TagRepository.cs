using Microsoft.Data.Sqlite;
using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Models.Enums;

namespace NoteApp.Data.Repositories;

/// <summary>标签数据访问实现（含便签-标签多对多关联 NoteTags）。</summary>
public class TagRepository : ITagRepository
{
    private const string TagColumns = "Id, Name, Color, CreatedAt";

    private readonly AppDbContext _dbContext;

    public TagRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Tag? GetById(int id)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TagColumns} FROM Tags WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? MapTag(reader) : null;
    }

    public List<Tag> GetAll()
    {
        return QueryTags($"SELECT {TagColumns} FROM Tags ORDER BY CreatedAt ASC;");
    }

    public Tag? GetByName(string name)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TagColumns} FROM Tags WHERE Name = $name;";
        command.Parameters.AddWithValue("$name", name);

        using var reader = command.ExecuteReader();
        return reader.Read() ? MapTag(reader) : null;
    }

    public Tag Create(Tag tag)
    {
        tag.CreatedAt = DateTime.Now;

        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Tags (Name, Color, CreatedAt) VALUES ($name, $color, $createdAt);";
        command.Parameters.AddWithValue("$name", tag.Name);
        command.Parameters.AddWithValue("$color", NoteColorHelper.ToStorage(tag.Color));
        command.Parameters.AddWithValue("$createdAt", DateTimeHelper.ToStorage(tag.CreatedAt));
        command.ExecuteNonQuery();

        using var idCommand = connection.CreateCommand();
        idCommand.CommandText = "SELECT last_insert_rowid();";
        tag.Id = Convert.ToInt32(idCommand.ExecuteScalar());

        return tag;
    }

    public Tag Update(Tag tag)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Tags SET Name = $name, Color = $color WHERE Id = $id;";
        command.Parameters.AddWithValue("$name", tag.Name);
        command.Parameters.AddWithValue("$color", NoteColorHelper.ToStorage(tag.Color));
        command.Parameters.AddWithValue("$id", tag.Id);
        command.ExecuteNonQuery();

        return tag;
    }

    public void Delete(int id)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Tags WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public int GetUsageCount(int tagId)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM NoteTags WHERE TagId = $tagId;";
        command.Parameters.AddWithValue("$tagId", tagId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void AddTagToNote(Guid noteId, int tagId)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO NoteTags (NoteId, TagId) VALUES ($noteId, $tagId);";
        command.Parameters.AddWithValue("$noteId", GuidHelper.ToStorage(noteId));
        command.Parameters.AddWithValue("$tagId", tagId);
        command.ExecuteNonQuery();
    }

    public void RemoveTagFromNote(Guid noteId, int tagId)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM NoteTags WHERE NoteId = $noteId AND TagId = $tagId;";
        command.Parameters.AddWithValue("$noteId", GuidHelper.ToStorage(noteId));
        command.Parameters.AddWithValue("$tagId", tagId);
        command.ExecuteNonQuery();
    }

    public List<Tag> GetTagsForNote(Guid noteId)
    {
        var sql = "SELECT t.Id, t.Name, t.Color, t.CreatedAt FROM Tags t " +
                  "INNER JOIN NoteTags nt ON nt.TagId = t.Id " +
                  "WHERE nt.NoteId = $noteId ORDER BY t.CreatedAt ASC;";
        return QueryTags(sql, ("$noteId", GuidHelper.ToStorage(noteId)));
    }

    public List<Guid> GetNoteIdsForTag(int tagId)
    {
        var ids = new List<Guid>();

        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT NoteId FROM NoteTags WHERE TagId = $tagId;";
        command.Parameters.AddWithValue("$tagId", tagId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }
        return ids;
    }

    public List<NoteTagLink> GetAllNoteTagLinks()
    {
        var links = new List<NoteTagLink>();

        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT NoteId, TagId FROM NoteTags;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            links.Add(new NoteTagLink
            {
                NoteId = Guid.Parse(reader.GetString(0)),
                TagId = reader.GetInt32(1),
            });
        }
        return links;
    }

    public void RemoveAllTagsForNote(Guid noteId)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM NoteTags WHERE NoteId = $noteId;";
        command.Parameters.AddWithValue("$noteId", GuidHelper.ToStorage(noteId));
        command.ExecuteNonQuery();
    }

    private List<Tag> QueryTags(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var tags = new List<Tag>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tags.Add(MapTag(reader));
        }
        return tags;
    }

    private static Tag MapTag(SqliteDataReader reader)
    {
        return new Tag
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            Color = NoteColorHelper.Parse(reader.GetString(2)),
            CreatedAt = DateTimeHelper.Parse(reader.GetString(3)),
        };
    }
}
