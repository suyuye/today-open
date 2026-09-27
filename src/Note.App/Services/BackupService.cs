using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.Data.Sqlite;

using NoteApp.Data;
using NoteApp.Helpers;
using NoteApp.Models;

namespace NoteApp.Services;

/// <summary>
/// 自动备份服务：把数据库备份到 Backup 目录，支持按间隔自动备份、手动备份、恢复与清理。
///
/// 备份方式用的是 SQLite 的 <c>VACUUM INTO</c>（而不是直接复制 .db 文件）：
/// 直接复制运行中的数据库文件有真实风险 —— SQLite 是按「页」就地修改文件的，
/// 一个事务可能跨多个页分多次落盘，拷贝若恰好发生在中间就会拿到「一半新页一半旧页」
/// 的库；而且只拷主文件会漏掉 <c>-wal</c> / <c>-journal</c> 附属文件，
/// 在 WAL 模式下更会静默丢掉已提交但尚未合并的内容。
/// 这种损坏没有任何外在征兆（文件大小正常、也能打开），只有恢复时才发现用不了。
/// <c>VACUUM INTO</c> 在**连接层**读取逻辑内容后写入新文件，自动尊重事务边界与锁，
/// 产出的快照永远自洽；顺带整理碎片，备份体积通常更小。
/// </summary>
public class BackupService : IBackupService
{
    private const string BackupFilePrefix = "today_open_";
    private const string BackupFileSuffix = ".db";
    private const string PreRestoreFileName = "today_open_pre_restore.db";

    /// <summary>SQLite 除主文件外的附属文件后缀。</summary>
    private static readonly string[] SidecarSuffixes = { "-wal", "-shm", "-journal" };

    /// <summary>备份文件名形如 today_open_20260925_203012.db（重名时补 _1）。</summary>
    private static readonly Regex BackupNamePattern = new(
        @"^today_open_(\d{8})_(\d{6})(?:_\d+)?\.db$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AppDbContext _dbContext;
    private readonly ISettingsService _settingsService;

    public string BackupDirectory => Path.Combine(_dbContext.DataDirectory, "Backup");

    public BackupService(AppDbContext dbContext, ISettingsService settingsService)
    {
        _dbContext = dbContext;
        _settingsService = settingsService;
    }

    public void CheckAndBackup()
    {
        Execute("CheckAndBackup", () =>
        {
            if (!_settingsService.BackupEnabled)
            {
                LogHelper.Info("自动备份检查完成：备份已禁用，跳过。");
                return;
            }

            var latest = GetLatestBackup();
            if (latest is not null && (DateTime.Now - latest.CreatedAt).TotalDays < _settingsService.BackupIntervalDays)
            {
                LogHelper.Info("自动备份检查完成：未到备份时间。");
                return;
            }

            LogHelper.Info("执行自动备份。");
            BackupNow();
        });
    }

    public void BackupNow()
    {
        Execute("BackupNow", () =>
        {
            Directory.CreateDirectory(BackupDirectory);

            var destPath = BuildBackupFilePath();
            CreateSnapshot(destPath);

            LogHelper.Info($"备份完成：{Path.GetFileName(destPath)}");
            CleanOldBackups();
        });
    }

    public List<BackupInfo> GetBackups() => Execute("GetBackups", EnumerateBackups);

    public bool IsValidBackup(string backupFilePath, out string? error)
    {
        error = null;

        try
        {
            if (!File.Exists(backupFilePath))
            {
                error = "备份文件不存在";
                return false;
            }

            // 只读 + 不池化：校验不该改动备份文件，也不该在池里留下句柄
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = backupFilePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            // 完整性检查：不是合法数据库 / 页损坏，都会在这里暴露
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA quick_check;";
                var result = command.ExecuteScalar() as string;
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"完整性校验未通过（{result ?? "无返回"}）";
                    return false;
                }
            }

            // 光能打开还不够，得确认里面确实是本应用的库
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Notes';";
                if (Convert.ToInt64(command.ExecuteScalar()) == 0)
                {
                    error = "备份中不含便签数据表";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void RestoreFromBackup(string backupFilePath)
    {
        Execute("RestoreFromBackup", () =>
        {
            if (!File.Exists(backupFilePath))
            {
                throw new FileNotFoundException("备份文件不存在", backupFilePath);
            }

            // 恢复前先留一份当前库，万一选错了备份还有退路
            if (File.Exists(_dbContext.DatabasePath))
            {
                var preRestorePath = Path.Combine(_dbContext.DataDirectory, PreRestoreFileName);
                File.Copy(_dbContext.DatabasePath, preRestorePath, overwrite: true);
            }

            // 释放连接池中的空闲连接：否则 SQLite 仍持有即将被覆盖的文件，
            // 可能出现「文件已换、内存页缓存还是旧的」这种不一致。
            SqliteConnection.ClearAllPools();
            DeleteSidecarFiles();

            File.Copy(backupFilePath, _dbContext.DatabasePath, overwrite: true);

            // 覆盖后必须再清一次：旧库可能遗留 -wal / -shm / -journal，
            // 它们与新库不匹配，SQLite 试图按旧日志恢复就会出问题。
            DeleteSidecarFiles();
            SqliteConnection.ClearAllPools();

            LogHelper.Info($"从备份恢复：{Path.GetFileName(backupFilePath)}");
        });
    }

    public void CleanOldBackups(int retainDays = 30)
    {
        Execute("CleanOldBackups", () =>
        {
            var cutoff = DateTime.Now.AddDays(-retainDays);
            var deleted = 0;
            foreach (var backup in EnumerateBackups())
            {
                if (backup.CreatedAt < cutoff)
                {
                    File.Delete(backup.FilePath);
                    deleted++;
                }
            }

            if (deleted > 0)
            {
                LogHelper.Info($"清理旧备份：删除 {deleted} 个备份文件");
            }
        });
    }

    /// <summary>
    /// 用 <c>VACUUM INTO</c> 生成一致性快照。目标文件必须不存在（存在会报错）。
    /// </summary>
    private void CreateSnapshot(string destPath)
    {
        if (File.Exists(destPath))
        {
            File.Delete(destPath);
        }

        using var connection = _dbContext.GetConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        // 路径由本类生成（时间戳文件名），这里仍转义单引号以杜绝拼接问题
        var literal = destPath.Replace("'", "''");
        command.CommandText = $"VACUUM INTO '{literal}';";
        command.ExecuteNonQuery();
    }

    /// <summary>生成一个尚未占用的备份文件完整路径（同一秒内重复备份时补序号）。</summary>
    private string BuildBackupFilePath()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var path = Path.Combine(BackupDirectory, $"{BackupFilePrefix}{stamp}{BackupFileSuffix}");

        var suffix = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(BackupDirectory, $"{BackupFilePrefix}{stamp}_{suffix}{BackupFileSuffix}");
            suffix++;
        }

        return path;
    }

    /// <summary>
    /// 删除 SQLite 附属文件（-wal / -shm / -journal）。
    /// 失败只记警告：附属文件删不掉不该阻断恢复主流程。
    /// </summary>
    private void DeleteSidecarFiles()
    {
        foreach (var suffix in SidecarSuffixes)
        {
            var path = _dbContext.DatabasePath + suffix;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Warning($"删除数据库附属文件失败 {path}：{ex.Message}");
            }
        }
    }

    /// <summary>遍历备份目录，按创建时间倒序返回备份列表。</summary>
    private List<BackupInfo> EnumerateBackups()
    {
        var backups = new List<BackupInfo>();
        if (!Directory.Exists(BackupDirectory))
        {
            return backups;
        }

        foreach (var file in Directory.GetFiles(BackupDirectory, "*.db"))
        {
            var info = new FileInfo(file);
            backups.Add(new BackupInfo
            {
                FileName = info.Name,
                FilePath = info.FullName,
                SizeBytes = info.Length,
                // 优先取文件名里的时间戳：它记录的是「备份生成时间」，
                // 而文件系统的创建时间在复制、移动、某些文件系统上并不可靠，
                // 这里的时间直接决定「保留期清理」删哪些文件，取错会误删。
                CreatedAt = TryParseStampFromName(info.Name) ?? info.CreationTime,
            });
        }

        backups.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return backups;
    }

    /// <summary>从 today_open_yyyyMMdd_HHmmss.db 解析备份时间，格式不符返回 null。</summary>
    private static DateTime? TryParseStampFromName(string fileName)
    {
        var match = BackupNamePattern.Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        return DateTime.TryParseExact(
            match.Groups[1].Value + match.Groups[2].Value,
            "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }

    private BackupInfo? GetLatestBackup()
    {
        var backups = EnumerateBackups();
        return backups.Count > 0 ? backups[0] : null;
    }

    private static T Execute<T>(string operation, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            LogHelper.Error($"BackupService.{operation} 执行失败", ex);
            throw;
        }
    }

    private static void Execute(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogHelper.Error($"BackupService.{operation} 执行失败", ex);
            throw;
        }
    }
}
