namespace NoteApp.Models;

/// <summary>
/// 便签的图片附件。
///
/// ⚠️ <see cref="Data"/> 只在「插入」与「按 Id 取字节」时被填充；
/// 列表查询（<c>GetForNote</c>）刻意**不读字节**，否则展示一条缩略图条
/// 就要把几 MB 的图片全部载入内存。
/// </summary>
public class Attachment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>所属便签。</summary>
    public Guid NoteId { get; set; }

    /// <summary>原始文件名（导入/另存时用），可能为空。</summary>
    public string? FileName { get; set; }

    /// <summary>MIME 类型，如 image/png。</summary>
    public string ContentType { get; set; } = "image/png";

    /// <summary>字节数（与 <see cref="Data"/> 的真实长度一致）。</summary>
    public long ByteSize { get; set; }

    public int PixelWidth { get; set; }

    public int PixelHeight { get; set; }

    /// <summary>图片字节。仅在插入与按 Id 取字节时填充。</summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
