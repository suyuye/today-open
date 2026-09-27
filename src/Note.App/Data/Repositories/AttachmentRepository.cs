using Microsoft.Data.Sqlite;

using NoteApp.Helpers;
using NoteApp.Models;

namespace NoteApp.Data.Repositories;

/// <summary>图片附件数据访问实现。</summary>
public class AttachmentRepository : IAttachmentRepository
{
    private const string MetaColumns =
        "Id, NoteId, FileName, ContentType, ByteSize, PixelWidth, PixelHeight, CreatedAt";

    private readonly AppDbContext _dbContext;

    public AttachmentRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Attachment Add(Attachment attachment)
    {
        if (attachment.Id == Guid.Empty)
        {
            attachment.Id = Guid.NewGuid();
        }

        attachment.ByteSize = attachment.Data.LongLength;
        attachment.CreatedAt = DateTime.Now;

        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO Attachments (Id, NoteId, FileName, ContentType, ByteSize, PixelWidth, PixelHeight, Data, CreatedAt) " +
            "VALUES ($id, $noteId, $fileName, $contentType, $byteSize, $w, $h, $data, $createdAt);";
        command.Parameters.AddWithValue("$id", GuidHelper.ToStorage(attachment.Id));
        command.Parameters.AddWithValue("$noteId", GuidHelper.ToStorage(attachment.NoteId));
        command.Parameters.AddWithValue("$fileName", (object?)attachment.FileName ?? DBNull.Value);
        command.Parameters.AddWithValue("$contentType", attachment.ContentType);
        command.Parameters.AddWithValue("$byteSize", attachment.ByteSize);
        command.Parameters.AddWithValue("$w", attachment.PixelWidth);
        command.Parameters.AddWithValue("$h", attachment.PixelHeight);
        command.Parameters.AddWithValue("$data", attachment.Data);
        command.Parameters.AddWithValue("$createdAt", DateTimeHelper.ToStorage(attachment.CreatedAt));
        command.ExecuteNonQuery();

        return attachment;
    }

    public List<Attachment> GetForNote(Guid noteId)
    {
        var items = new List<Attachment>();

        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {MetaColumns} FROM Attachments WHERE NoteId = $noteId ORDER BY CreatedAt ASC;";
        command.Parameters.AddWithValue("$noteId", GuidHelper.ToStorage(noteId));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(Map(reader));
        }

        return items;
    }

    public Attachment? GetData(Guid id)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {MetaColumns}, Data FROM Attachments WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", GuidHelper.ToStorage(id));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var attachment = Map(reader);
        attachment.Data = (byte[])reader.GetValue(8);
        return attachment;
    }

    public int CountForNote(Guid noteId)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Attachments WHERE NoteId = $noteId;";
        command.Parameters.AddWithValue("$noteId", GuidHelper.ToStorage(noteId));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Delete(Guid id)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Attachments WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", GuidHelper.ToStorage(id));
        command.ExecuteNonQuery();
    }

    public AttachmentStorageSummary GetStorageSummary()
    {
        // 一条 SELECT 同时取两组数字：
        //   全表：COUNT(*)、SUM(ByteSize)
        //   孤儿：NoteId 在 Notes 表里已不存在（便签被永久删除后没清理附件表）
        // 用 LEFT JOIN + IS NULL 判孤儿，而不是 NOT IN —— 后者在有 NULL 时行为反直觉，
        // 且大表上 NOT IN 的执行计划更差。
        //
        // ⚠️ 回收站（SoftDelete）里的便签不算孤儿：便签还能还原，图片必须跟着活下来。
        // Notes 表物理行还在（只是 DeletedAt 非空），LEFT JOIN 仍能连上 → 不计入孤儿。
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*), " +
            "       COALESCE(SUM(a.ByteSize), 0), " +
            "       COUNT(n.Id), " +
            "       COALESCE(SUM(CASE WHEN n.Id IS NULL THEN a.ByteSize ELSE 0 END), 0) " +
            "FROM Attachments a " +
            "LEFT JOIN Notes n ON n.Id = a.NoteId;";
        using var reader = command.ExecuteReader();
        reader.Read();
        return new AttachmentStorageSummary
        {
            // COUNT(n.Id) 统计的是能连上便签的行数（即非孤儿数）；
            // 孤儿数 = 总数 - 非孤儿数。SUM(CASE...) 只累加孤儿行的字节。
            TotalCount = Convert.ToInt32(reader.GetInt64(0)),
            TotalBytes = reader.GetInt64(1),
            OrphanCount = Convert.ToInt32(reader.GetInt64(0)) - Convert.ToInt32(reader.GetInt64(2)),
            OrphanBytes = reader.GetInt64(3),
        };
    }

    public int DeleteOrphans()
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DELETE FROM Attachments " +
            "WHERE NoteId NOT IN (SELECT Id FROM Notes);";
        var affected = command.ExecuteNonQuery();
        transaction.Commit();
        return affected;
    }

    private static Attachment Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        NoteId = Guid.Parse(reader.GetString(1)),
        FileName = reader.IsDBNull(2) ? null : reader.GetString(2),
        ContentType = reader.GetString(3),
        ByteSize = reader.GetInt64(4),
        PixelWidth = reader.GetInt32(5),
        PixelHeight = reader.GetInt32(6),
        CreatedAt = DateTimeHelper.Parse(reader.GetString(7)),
    };
}
