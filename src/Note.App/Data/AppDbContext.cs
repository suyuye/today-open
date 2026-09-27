using System.IO;
using Microsoft.Data.Sqlite;
using NoteApp.Helpers;

namespace NoteApp.Data;

/// <summary>
/// SQLite 连接管理。作为单例注册，每次数据操作通过 <see cref="GetConnection"/> 获取新连接。
/// 连接串启用外键约束（等价于每条连接 PRAGMA foreign_keys = ON），确保 ON DELETE CASCADE 生效。
/// </summary>
public class AppDbContext
{
    /// <summary>数据库统一存储的时间格式（ISO 8601，本地时间）。</summary>
    private const string Iso8601Format = "yyyy-MM-ddTHH:mm:ss";

    private readonly string _connectionString;

    /// <summary>数据目录（数据库文件与备份目录的父目录）。</summary>
    public string DataDirectory { get; }

    /// <summary>数据库文件完整路径。</summary>
    public string DatabasePath { get; }

    public AppDbContext()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "今日营业中");
        Directory.CreateDirectory(directory);

        DataDirectory = directory;
        DatabasePath = Path.Combine(directory, "today_open.db");

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            ForeignKeys = true,
        }.ToString();
    }

    /// <summary>创建一个新的 SQLite 连接（调用方负责释放）。</summary>
    public SqliteConnection GetConnection() => new(_connectionString);

    /// <summary>初始化数据库：建表、建索引、写入预设设置项。应用启动时调用一次，重复调用安全（全部幂等）。</summary>
    public void InitializeDatabase()
    {
        using var connection = GetConnection();
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = CreateSchemaSql;
            command.ExecuteNonQuery();
        }

        MigrateSchema(connection);
        SeedDefaultSettings(connection);

        LogHelper.Info("数据库初始化完成。");
    }

    /// <summary>对旧数据库执行增量迁移（幂等）：补加缺失列。每次启动都检查。</summary>
    private static void MigrateSchema(SqliteConnection connection)
    {
        if (!ColumnExists(connection, "Notes", "Title"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE Notes ADD COLUMN Title TEXT;";
            command.ExecuteNonQuery();
            LogHelper.Info("数据库迁移：Notes 表新增 Title 列。");
        }

        if (!ColumnExists(connection, "Notes", "IsMinimal"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE Notes ADD COLUMN IsMinimal INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();
            LogHelper.Info("数据库迁移：Notes 表新增 IsMinimal 列。");
        }

        if (!ColumnExists(connection, "Notes", "IsDockEnabled"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE Notes ADD COLUMN IsDockEnabled INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();
            LogHelper.Info("数据库迁移：Notes 表新增 IsDockEnabled 列。");
        }

        if (!ColumnExists(connection, "Notes", "DockEdge"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE Notes ADD COLUMN DockEdge INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();
            LogHelper.Info("数据库迁移：Notes 表新增 DockEdge 列。");
        }

        if (!ColumnExists(connection, "Notes", "IsDogEar"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE Notes ADD COLUMN IsDogEar INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();
            LogHelper.Info("数据库迁移：Notes 表新增 IsDogEar 列。");
        }
    }

    /// <summary>判断表是否存在指定列（PRAGMA table_info 结果集中匹配 name）。</summary>
    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // table_info 列顺序：cid(0), name(1), type(2), notnull(3), dflt_value(4), pk(5)
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void SeedDefaultSettings(SqliteConnection connection)
    {
        var now = DateTime.Now.ToString(Iso8601Format);

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO Settings (Key, Value, UpdatedAt) VALUES ($key, $value, $updatedAt);";
        var keyParam = command.Parameters.Add("$key", SqliteType.Text);
        var valueParam = command.Parameters.Add("$value", SqliteType.Text);
        var updatedParam = command.Parameters.Add("$updatedAt", SqliteType.Text);
        updatedParam.Value = now;

        foreach (var (key, value) in DefaultSettings)
        {
            keyParam.Value = key;
            valueParam.Value = value;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>预设设置项（键、默认值），供数据库初始化与设置重置共用。</summary>
    internal static readonly (string Key, string Value)[] DefaultSettings =
    {
        ("general.first_run_completed", "false"),
        ("general.autostart", "true"),
        ("general.startup_delay_seconds", "5"),
        ("general.minimize_to_tray", "true"),
        ("general.new_note_position", "mouse"),
        ("general.autosave_interval_ms", "500"),
        ("general.pinned_no_focus", "true"),
        ("appearance.theme", "light"),
        ("appearance.default_color", "yellow"),
        ("appearance.default_opacity", "100"),
        ("appearance.font_size", "16.5"),
        ("appearance.note_font", "handwriting"),
        ("reminder.sound_enabled", "true"),
        ("reminder.snooze_minutes", "10"),
        ("hotkey.new_note", "Ctrl+Alt+N"),
        ("hotkey.search", "Ctrl+Alt+F"),
        ("hotkey.toggle_all", "Ctrl+Alt+H"),
        ("data.backup_enabled", "true"),
        ("data.backup_interval_days", "7"),
        ("data.retain_days", "30"),
    };

    private const string CreateSchemaSql = """
        CREATE TABLE IF NOT EXISTS Notes (
            Id TEXT PRIMARY KEY,
            Title TEXT,
            Content TEXT NOT NULL DEFAULT '',
            Color TEXT NOT NULL DEFAULT 'yellow',
            PositionX INTEGER NOT NULL DEFAULT 100,
            PositionY INTEGER NOT NULL DEFAULT 100,
            Width INTEGER NOT NULL DEFAULT 220,
            Height INTEGER NOT NULL DEFAULT 200,
            Opacity INTEGER NOT NULL DEFAULT 100,
            IsPinned INTEGER NOT NULL DEFAULT 0,
            IsLocked INTEGER NOT NULL DEFAULT 0,
            IsArchived INTEGER NOT NULL DEFAULT 0,
            IsCollapsed INTEGER NOT NULL DEFAULT 0,
            IsMinimal INTEGER NOT NULL DEFAULT 0,
            IsDockEnabled INTEGER NOT NULL DEFAULT 0,
            DockEdge INTEGER NOT NULL DEFAULT 0,
            IsDogEar INTEGER NOT NULL DEFAULT 0,
            FontSize REAL,
            ZIndex INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            DeletedAt TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_notes_deleted ON Notes(DeletedAt);
        CREATE INDEX IF NOT EXISTS idx_notes_pinned ON Notes(IsPinned);
        CREATE INDEX IF NOT EXISTS idx_notes_archived ON Notes(IsArchived);
        CREATE INDEX IF NOT EXISTS idx_notes_updated ON Notes(UpdatedAt DESC);

        CREATE TABLE IF NOT EXISTS Tags (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL UNIQUE,
            Color TEXT NOT NULL DEFAULT 'gray',
            CreatedAt TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS NoteTags (
            NoteId TEXT NOT NULL,
            TagId INTEGER NOT NULL,
            PRIMARY KEY (NoteId, TagId),
            FOREIGN KEY (NoteId) REFERENCES Notes(Id) ON DELETE CASCADE,
            FOREIGN KEY (TagId) REFERENCES Tags(Id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS Reminders (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            NoteId TEXT NOT NULL,
            RemindAt TEXT NOT NULL,
            RepeatRule TEXT NOT NULL DEFAULT 'none',
            IsCompleted INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (NoteId) REFERENCES Notes(Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_reminders_note ON Reminders(NoteId);
        CREATE INDEX IF NOT EXISTS idx_reminders_time ON Reminders(RemindAt);

        -- 便签的图片附件。
        -- 图片字节直接存在数据库里（BLOB）而不是外部文件夹，这是刻意的取舍：
        -- 备份用的是 VACUUM INTO，它会把整库原子地复制一份，
        -- 因此**图片自动跟随备份、也自动跟随恢复**，不需要额外维护附件目录，
        -- 更不会出现「备份看着成功、恢复后图片全没了」或孤儿文件。
        -- 代价是库文件变大，已在写入前做等比缩小与编码压缩来控制体积。
        CREATE TABLE IF NOT EXISTS Attachments (
            Id TEXT PRIMARY KEY,
            NoteId TEXT NOT NULL,
            FileName TEXT,
            ContentType TEXT NOT NULL,
            ByteSize INTEGER NOT NULL,
            PixelWidth INTEGER NOT NULL DEFAULT 0,
            PixelHeight INTEGER NOT NULL DEFAULT 0,
            Data BLOB NOT NULL,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (NoteId) REFERENCES Notes(Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_attachments_note ON Attachments(NoteId);

        CREATE TABLE IF NOT EXISTS Settings (
            Key TEXT PRIMARY KEY,
            Value TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        """;
}
