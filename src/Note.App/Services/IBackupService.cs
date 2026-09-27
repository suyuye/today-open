using NoteApp.Models;

namespace NoteApp.Services;

/// <summary>自动备份服务。</summary>
public interface IBackupService
{
    /// <summary>备份目录完整路径（用于「打开备份文件夹」）。</summary>
    string BackupDirectory { get; }

    /// <summary>检查是否到期，到期则备份。</summary>
    void CheckAndBackup();

    /// <summary>立即备份一次。</summary>
    void BackupNow();

    /// <summary>获取所有备份文件列表（按创建时间倒序）。</summary>
    List<BackupInfo> GetBackups();

    /// <summary>
    /// 校验备份文件是否为可用的数据库（含完整性检查与数据表存在性）。
    /// 恢复前必须先过这一关 —— 用坏文件覆盖好数据是不可逆的。
    /// </summary>
    bool IsValidBackup(string backupFilePath, out string? error);

    /// <summary>
    /// 从指定备份文件恢复（覆盖当前数据库）。
    /// ⚠️ 调用前应确保所有写入方已停止：恢复后内存中的旧数据若再落库，会把恢复结果覆盖掉。
    /// 正确用法是在应用退出流程的末尾调用，随后重新启动应用。
    /// </summary>
    void RestoreFromBackup(string backupFilePath);

    /// <summary>清理超过保留天数的备份。</summary>
    void CleanOldBackups(int retainDays = 30);
}
