using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using NoteApp.Data.Repositories;
using NoteApp.Helpers;
using NoteApp.Models;

namespace NoteApp.Services;

/// <summary>便签图片附件服务。</summary>
public interface IAttachmentService
{
    /// <summary>从文件添加图片（会等比缩小并重新编码）。无法识别的图片返回 null。</summary>
    Attachment? AddImageFromFile(Guid noteId, string filePath);

    /// <summary>从剪贴板添加图片；剪贴板里没有图片时返回 null。</summary>
    Attachment? AddImageFromClipboard(Guid noteId);

    /// <summary>取某条便签的附件元数据（不含字节）。</summary>
    List<Attachment> GetForNote(Guid noteId);

    /// <summary>取附件字节（用于显示大图或另存）。</summary>
    byte[]? GetData(Guid attachmentId);

    void Delete(Guid attachmentId);

    /// <summary>把附件写到磁盘。</summary>
    bool SaveToFile(Guid attachmentId, string targetPath);

    /// <summary>附件存储统计（供设置页「附件管理」展示）。</summary>
    AttachmentStorageSummary GetStorageSummary();

    /// <summary>删除孤儿附件（所属便签已被永久删除），返回删除条数。</summary>
    int DeleteOrphans();
}

/// <summary>
/// 图片附件实现。
///
/// 两个关键取舍：
/// 1. **图片存进数据库（BLOB）**。这样备份用的 VACUUM INTO 会自动、原子地
///    把图片一起带上，恢复亦然 —— 不需要单独维护附件目录，也不会出现
///    孤儿文件或「备份成功但图片丢失」。
/// 2. **写入前统一缩小并重编码**。直接存原图会让库文件迅速膨胀（进而放大每一份备份），
///    所以最长边压到 <see cref="ImageSizing.MaxDimension"/>，优先 PNG（截图文字清晰），
///    体积过大时再退到 JPEG 压缩。
/// </summary>
public class AttachmentService : IAttachmentService
{
    /// <summary>PNG 超过这个体积就改用 JPEG 压——照片类图片用 PNG 存会大得离谱。</summary>
    private const int PngSizeThreshold = 4 * 1024 * 1024;

    private const int JpegQuality = 85;

    private readonly IAttachmentRepository _repository;

    public AttachmentService(IAttachmentRepository repository)
    {
        _repository = repository;
    }

    public Attachment? AddImageFromFile(Guid noteId, string filePath)
    {
        try
        {
            var source = LoadFromFile(filePath);
            if (source is null)
            {
                LogHelper.Warning($"添加图片失败：无法解码 {filePath}");
                return null;
            }

            return Store(noteId, source, Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            LogHelper.Error($"添加图片失败：{filePath}", ex);
            return null;
        }
    }

    public Attachment? AddImageFromClipboard(Guid noteId)
    {
        var source = ReadClipboardImage();
        if (source is null)
        {
            return null;
        }

        try
        {
            return Store(noteId, source, null);
        }
        catch (Exception ex)
        {
            LogHelper.Error("从剪贴板添加图片失败", ex);
            return null;
        }
    }

    public List<Attachment> GetForNote(Guid noteId) => _repository.GetForNote(noteId);

    public byte[]? GetData(Guid attachmentId) => _repository.GetData(attachmentId)?.Data;

    public void Delete(Guid attachmentId)
    {
        _repository.Delete(attachmentId);
        LogHelper.Info($"删除图片附件 {attachmentId}");
    }

    public bool SaveToFile(Guid attachmentId, string targetPath)
    {
        var attachment = _repository.GetData(attachmentId);
        if (attachment is null)
        {
            return false;
        }

        try
        {
            File.WriteAllBytes(targetPath, attachment.Data);
            return true;
        }
        catch (Exception ex)
        {
            LogHelper.Error($"导出图片失败：{targetPath}", ex);
            return false;
        }
    }

    public AttachmentStorageSummary GetStorageSummary() => _repository.GetStorageSummary();

    public int DeleteOrphans()
    {
        var deleted = _repository.DeleteOrphans();
        if (deleted > 0)
        {
            LogHelper.Info($"清理孤儿附件 {deleted} 条");
        }
        return deleted;
    }

    /// <summary>缩放 + 编码 + 落库。</summary>
    private Attachment Store(Guid noteId, BitmapSource source, string? fileName)
    {
        var (data, contentType, width, height) = Encode(source);

        var attachment = new Attachment
        {
            NoteId = noteId,
            FileName = fileName,
            ContentType = contentType,
            PixelWidth = width,
            PixelHeight = height,
            Data = data,
        };

        _repository.Add(attachment);
        LogHelper.Info($"添加图片附件：{width}x{height}，{data.Length / 1024} KB，{contentType}");
        return attachment;
    }

    /// <summary>等比缩小后编码；PNG 优先，过大时改用 JPEG（取更小的那个）。</summary>
    private static (byte[] Data, string ContentType, int Width, int Height) Encode(BitmapSource source)
    {
        var (width, height) = ImageSizing.Fit(source.PixelWidth, source.PixelHeight);

        BitmapSource image = source;
        if (width != source.PixelWidth || height != source.PixelHeight)
        {
            var scale = (double)width / source.PixelWidth;
            var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            scaled.Freeze();
            image = scaled;
        }

        var png = EncodeWith(new PngBitmapEncoder(), image);
        if (png.Length <= PngSizeThreshold)
        {
            return (png, "image/png", width, height);
        }

        var jpeg = EncodeWith(new JpegBitmapEncoder { QualityLevel = JpegQuality }, image);
        return jpeg.Length < png.Length
            ? (jpeg, "image/jpeg", width, height)
            : (png, "image/png", width, height);
    }

    private static byte[] EncodeWith(BitmapEncoder encoder, BitmapSource image)
    {
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// 从文件解码。<c>OnLoad</c> 让文件立刻被读完并关闭句柄，
    /// 否则图片文件会被本进程一直占用，用户连删除都删不掉。
    /// </summary>
    private static BitmapSource? LoadFromFile(string filePath)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(filePath, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>读剪贴板里的图片。剪贴板可能被别的进程短暂占用，故重试一次。</summary>
    private static BitmapSource? ReadClipboardImage()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!Clipboard.ContainsImage())
                {
                    return null;
                }

                var image = Clipboard.GetImage();
                if (image is null)
                {
                    return null;
                }

                if (image.CanFreeze)
                {
                    image.Freeze();
                }

                return image;
            }
            catch (Exception ex) when (attempt == 0)
            {
                LogHelper.Warning($"读取剪贴板图片失败，重试一次：{ex.Message}");
            }
            catch (Exception ex)
            {
                LogHelper.Error("读取剪贴板图片失败", ex);
                return null;
            }
        }

        return null;
    }
}
