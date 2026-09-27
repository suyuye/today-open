namespace NoteApp.Models;

/// <summary>备份文件信息（只读展示用）。</summary>
public class BackupInfo
{
    /// <summary>备份文件名（含扩展名）。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>备份文件完整路径。</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>文件大小（字节）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; }
}
