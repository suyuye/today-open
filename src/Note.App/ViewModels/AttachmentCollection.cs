using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Services;

namespace NoteApp.ViewModels;

/// <summary>缩略图条里的一张图片。</summary>
public partial class AttachmentItemViewModel : ViewModelBase
{
    /// <summary>缩略图解码宽度。显示尺寸是 72px，按 2 倍解码以适配高分屏。</summary>
    private const int ThumbnailDecodeWidth = 144;

    private readonly Action<AttachmentItemViewModel> _onDelete;

    public AttachmentItemViewModel(Attachment meta, Action<AttachmentItemViewModel> onDelete)
    {
        Meta = meta;
        _onDelete = onDelete;
    }

    public Attachment Meta { get; }

    /// <summary>缩略图（按需从字节解码，解码宽度受限，避免为缩略图载入整张原图）。</summary>
    [ObservableProperty]
    private ImageSource? _thumbnail;

    /// <summary>鼠标悬停提示：文件名、像素尺寸与体积。</summary>
    public string Description
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Meta.FileName) ? "图片" : Meta.FileName;
            var size = Meta.ByteSize >= 1024 * 1024
                ? $"{Meta.ByteSize / 1024.0 / 1024.0:0.#} MB"
                : $"{Math.Max(1, Meta.ByteSize / 1024)} KB";
            return $"{name}\n{Meta.PixelWidth}×{Meta.PixelHeight} · {size}";
        }
    }

    /// <summary>用附件字节生成缩略图。</summary>
    public void LoadThumbnail(byte[] data)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(data);
            image.CacheOption = BitmapCacheOption.OnLoad;   // 立刻读完，Stream 可被释放
            image.DecodePixelWidth = ThumbnailDecodeWidth;  // 只解码到缩略图所需尺寸
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            Thumbnail = image;
        }
        catch (Exception ex)
        {
            // 单张图片解不出来不该影响整条缩略图条
            LogHelper.Error($"缩略图解码失败：{Meta.Id}", ex);
        }
    }

    [RelayCommand]
    private void Delete() => _onDelete(this);
}

/// <summary>
/// 便签图片附件的展示集合。
///
/// 特意做成一个**独立组件**而不是在两个 ViewModel 里各写一份：
/// 便签窗口（<see cref="NoteWindowViewModel"/>）与主页编辑区
/// （<see cref="NotePreviewViewModel"/>）需要完全相同的附件行为，
/// 而这个项目已经吃过「两处重复实现、改一处漏一处」的亏
/// （样式绑定漏加属性导致复选框隐形）。这次从一开始就共用同一份实现。
/// </summary>
public sealed class AttachmentCollection : ViewModelBase
{
    private readonly IAttachmentService _service;

    public AttachmentCollection(IAttachmentService service)
    {
        _service = service;
    }

    public ObservableCollection<AttachmentItemViewModel> Items { get; } = new();

    /// <summary>没有图片时整条隐藏，不让空白区域占地方。</summary>
    public Visibility Visibility => Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>当前所属便签。</summary>
    public Guid NoteId { get; private set; }

    /// <summary>从数据库重新载入（切换便签时调用）。</summary>
    public void Reload(Guid noteId)
    {
        NoteId = noteId;
        Items.Clear();

        List<Attachment> metas;
        try
        {
            metas = _service.GetForNote(noteId);
        }
        catch (Exception ex)
        {
            LogHelper.Error($"载入图片附件失败：{noteId}", ex);
            OnPropertyChanged(nameof(Visibility));
            return;
        }

        foreach (var meta in metas)
        {
            var item = new AttachmentItemViewModel(meta, Delete);
            var data = _service.GetData(meta.Id);
            if (data is not null)
            {
                item.LoadThumbnail(data);
            }
            Items.Add(item);
        }

        OnPropertyChanged(nameof(Visibility));
    }

    /// <summary>
    /// 从剪贴板添加图片；剪贴板里没有图片时返回 false（调用方据此决定要不要走原来的文本粘贴）。
    /// </summary>
    public bool TryAddFromClipboard()
    {
        if (NoteId == Guid.Empty)
        {
            return false;
        }

        var added = _service.AddImageFromClipboard(NoteId);
        if (added is null)
        {
            return false;
        }

        AppendItems(new[] { added });
        return true;
    }

    /// <summary>从文件添加图片，返回实际加入的张数。</summary>
    public int AddFromFiles(IEnumerable<string> filePaths)
    {
        if (NoteId == Guid.Empty)
        {
            return 0;
        }

        var added = new List<Attachment>();
        foreach (var path in filePaths)
        {
            var attachment = _service.AddImageFromFile(NoteId, path);
            if (attachment is not null)
            {
                added.Add(attachment);
            }
        }

        AppendItems(added);
        return added.Count;
    }

    /// <summary>取附件字节（用于「另存为」或查看大图）。</summary>
    public byte[]? GetData(Guid attachmentId) => _service.GetData(attachmentId);

    private void AppendItems(IReadOnlyList<Attachment> added)
    {
        foreach (var meta in added)
        {
            var item = new AttachmentItemViewModel(meta, Delete);
            if (meta.Data.Length > 0)
            {
                item.LoadThumbnail(meta.Data);
            }
            Items.Add(item);
        }

        OnPropertyChanged(nameof(Visibility));
    }

    private void Delete(AttachmentItemViewModel item)
    {
        _service.Delete(item.Meta.Id);
        Items.Remove(item);
        OnPropertyChanged(nameof(Visibility));
    }
}
