using NoteApp.Models;
using NoteApp.Services;
using NoteApp.ViewModels;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 图片附件集合的测试。
///
/// 重点覆盖「没有图片时整条不占位」与删除后集合状态 ——
/// 缩略图条的显隐由 <see cref="AttachmentCollection.Visibility"/> 决定，
/// 它必须在任何一次增删之后都被刷新，否则会出现
/// 「删完最后一张图，缩略图条还杵在那儿」这种明显的空条。
/// </summary>
public class AttachmentCollectionTests
{
    private static Attachment MakeMeta(string name = "a.png", int bytes = 1024)
        => new()
        {
            Id = Guid.NewGuid(),
            NoteId = Guid.NewGuid(),
            FileName = name,
            ContentType = "image/png",
            ByteSize = bytes,
            PixelWidth = 10,
            PixelHeight = 10,
            Data = Array.Empty<byte>(),
            CreatedAt = DateTime.Now,
        };

    [Fact]
    public void 初始为空时整条不显示()
    {
        var svc = new FakeAttachmentService();
        var col = new AttachmentCollection(svc);

        Assert.Empty(col.Items);
        Assert.Equal(System.Windows.Visibility.Collapsed, col.Visibility);
    }

    [Fact]
    public void 载入到图片后显示()
    {
        var svc = new FakeAttachmentService();
        var noteId = Guid.NewGuid();
        svc.Add(noteId, MakeMeta("one.png"));
        svc.Add(noteId, MakeMeta("two.png"));

        var col = new AttachmentCollection(svc);
        col.Reload(noteId);

        Assert.Equal(2, col.Items.Count);
        Assert.Equal(System.Windows.Visibility.Visible, col.Visibility);
        Assert.Equal(noteId, col.NoteId);
    }

    [Fact]
    public void 切换到没有图片的便签会隐藏()
    {
        var svc = new FakeAttachmentService();
        var withImages = Guid.NewGuid();
        var empty = Guid.NewGuid();
        svc.Add(withImages, MakeMeta());

        var col = new AttachmentCollection(svc);
        col.Reload(withImages);
        Assert.Equal(System.Windows.Visibility.Visible, col.Visibility);

        col.Reload(empty);
        Assert.Empty(col.Items);
        Assert.Equal(System.Windows.Visibility.Collapsed, col.Visibility);
    }

    [Fact]
    public void 删除最后一张图片后隐藏()
    {
        var svc = new FakeAttachmentService();
        var noteId = Guid.NewGuid();
        svc.Add(noteId, MakeMeta("only.png"));

        var col = new AttachmentCollection(svc);
        col.Reload(noteId);
        Assert.Equal(System.Windows.Visibility.Visible, col.Visibility);

        // 走命令，模拟界面点删除
        col.Items[0].DeleteCommand.Execute(null);

        Assert.Empty(col.Items);
        Assert.Equal(System.Windows.Visibility.Collapsed, col.Visibility);
        Assert.Single(svc.Deleted);
    }

    [Fact]
    public void 便签未载入时不接受粘贴()
    {
        // NoteId 还是 Guid.Empty（尚未 Reload），此时贴图应当被拒绝而不是写进库里
        var svc = new FakeAttachmentService();
        var col = new AttachmentCollection(svc);

        Assert.False(col.TryAddFromClipboard());
        Assert.Empty(svc.Added);
    }

    [Fact]
    public void 未载入便签时从文件添加返回零()
    {
        var svc = new FakeAttachmentService();
        var col = new AttachmentCollection(svc);

        Assert.Equal(0, col.AddFromFiles(new[] { @"C:\nope.png" }));
    }

    [Fact]
    public void 描述文本包含文件名与体积()
    {
        var meta = MakeMeta("黄昏.png", bytes: 2 * 1024 * 1024);
        var item = new AttachmentItemViewModel(meta, _ => { });

        var desc = item.Description;

        Assert.Contains("黄昏.png", desc);
        Assert.Contains("MB", desc);
    }

    [Fact]
    public void 文件名为空时用兜底名称()
    {
        var meta = MakeMeta(name: "");
        var item = new AttachmentItemViewModel(meta, _ => { });

        Assert.StartsWith("图片", item.Description);
    }

    [Fact]
    public void 小体积显示为KB且至少1KB()
    {
        var meta = MakeMeta(bytes: 10);
        var item = new AttachmentItemViewModel(meta, _ => { });

        // 10 字节若直接取整会显示 0 KB，读起来像坏图；这里应至少是 1 KB
        Assert.Contains("1 KB", item.Description);
    }
}

/// <summary>
/// 记录调用的 IAttachmentService 替身。
/// 未用到的成员抛异常，避免测试悄悄走到意外路径。
/// </summary>
internal sealed class FakeAttachmentService : IAttachmentService
{
    private readonly Dictionary<Guid, List<Attachment>> _byNote = new();

    /// <summary>已被删除的附件 Id。</summary>
    public List<Guid> Deleted { get; } = new();

    /// <summary>通过「粘贴」或「从文件」加进来的附件。</summary>
    public List<Attachment> Added { get; } = new();

    /// <summary>预设某条便签的附件（模拟库里已有）。</summary>
    public void Add(Guid noteId, Attachment meta)
    {
        meta.NoteId = noteId;
        if (!_byNote.TryGetValue(noteId, out var list))
        {
            list = new List<Attachment>();
            _byNote[noteId] = list;
        }
        list.Add(meta);
    }

    public List<Attachment> GetForNote(Guid noteId) =>
        _byNote.TryGetValue(noteId, out var list)
            ? list.Where(a => !Deleted.Contains(a.Id)).ToList()
            : new List<Attachment>();

    public byte[]? GetData(Guid attachmentId) => Array.Empty<byte>();

    public Attachment? AddImageFromFile(Guid noteId, string filePath)
    {
        var added = new Attachment
        {
            Id = Guid.NewGuid(),
            NoteId = noteId,
            FileName = System.IO.Path.GetFileName(filePath),
            ContentType = "image/png",
            ByteSize = 100,
            Data = new byte[] { 1, 2, 3 },
            CreatedAt = DateTime.Now,
        };
        Added.Add(added);
        Add(noteId, added);
        return added;
    }

    public Attachment? AddImageFromClipboard(Guid noteId)
    {
        var added = new Attachment
        {
            Id = Guid.NewGuid(),
            NoteId = noteId,
            FileName = "clipboard.png",
            ContentType = "image/png",
            ByteSize = 100,
            Data = new byte[] { 1, 2, 3 },
            CreatedAt = DateTime.Now,
        };
        Added.Add(added);
        Add(noteId, added);
        return added;
    }

    public void Delete(Guid attachmentId) => Deleted.Add(attachmentId);

    // ---- 以下成员本套测试未使用 ----

    public bool SaveToFile(Guid attachmentId, string targetPath) => throw new NotSupportedException();

    public Models.AttachmentStorageSummary GetStorageSummary() => throw new NotSupportedException();

    public int DeleteOrphans() => throw new NotSupportedException();
}
