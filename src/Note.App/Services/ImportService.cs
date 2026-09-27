using System.IO;
using System.Text.Json;
using System.Windows;

using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Models.Enums;

namespace NoteApp.Services;

/// <summary>便签导入服务。</summary>
public interface IImportService
{
    /// <summary>
    /// 从本应用「导出便签为 JSON」生成的文件导入（可往返）。
    /// 已存在相同 Id 的便签会被跳过，不会覆盖现有数据。
    /// </summary>
    ImportResult ImportFromJson(string filePath, Rect screen);

    /// <summary>从 Markdown / 纯文本文件导入，**每个文件一条便签**（保留 <c>- [ ]</c> 待办语法）。</summary>
    ImportResult ImportFromTextFiles(IReadOnlyList<string> filePaths, NoteColor color, Rect screen);
}

/// <summary>
/// 导入实现。
///
/// 设计要点：
/// 1. **入口宽松、落库严格** —— 外部文件可能被手改过，字段缺失或类型异常都不该炸掉整个导入，
///    但进库前必须经过 <see cref="NoteImportSanitizer"/>，否则会出现「导入成功却看不见」的便签。
/// 2. **单事务** —— 几百条导入到一半失败，留下「导了一半」的状态比整体失败更难收拾。
/// 3. **不覆盖既有数据** —— 相同 Id 一律跳过（宁可少导，不可误改）。
/// </summary>
public class ImportService : IImportService
{
    /// <summary>解析时保持宽容：允许注释与尾随逗号，便于用户手工整理导出文件。</summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly INoteService _noteService;

    public ImportService(INoteService noteService)
    {
        _noteService = noteService;
    }

    public ImportResult ImportFromJson(string filePath, Rect screen)
    {
        var result = new ImportResult();

        List<NoteDto>? dtos;
        try
        {
            var json = File.ReadAllText(filePath);
            dtos = JsonSerializer.Deserialize<List<NoteDto>>(json, ReadOptions);
        }
        catch (Exception ex)
        {
            LogHelper.Error($"导入 JSON 失败：{filePath}", ex);
            result.Failures.Add($"无法读取「{Path.GetFileName(filePath)}」：{ex.Message}");
            return result;
        }

        if (dtos is null || dtos.Count == 0)
        {
            result.Failures.Add("文件里没有可导入的便签。");
            return result;
        }

        var notes = new List<Note>(dtos.Count);
        for (var i = 0; i < dtos.Count; i++)
        {
            var dto = dtos[i];
            var note = ToNote(dto);

            // 文件里没写坐标时不要留在 (0,0)（会压在屏幕角落），直接层叠摆放
            if (dto.PositionX is null || dto.PositionY is null)
            {
                NoteImportSanitizer.PlaceCascade(note, screen, i);
            }

            NoteImportSanitizer.Sanitize(note, screen, i);
            notes.Add(note);
        }

        var (inserted, skipped) = _noteService.ImportNotes(notes);
        result.Imported = inserted;
        result.Skipped = skipped;

        LogHelper.Info($"导入 JSON：{Path.GetFileName(filePath)}，共 {dtos.Count} 条，"
                       + $"导入 {inserted}、跳过 {skipped}");
        return result;
    }

    public ImportResult ImportFromTextFiles(IReadOnlyList<string> filePaths, NoteColor color, Rect screen)
    {
        var result = new ImportResult();
        var notes = new List<Note>(filePaths.Count);

        for (var i = 0; i < filePaths.Count; i++)
        {
            var path = filePaths[i];
            try
            {
                var text = File.ReadAllText(path);
                var note = NoteImportSanitizer.FromTextFile(path, text, color);

                // 文本文件没有坐标信息，统一层叠摆放
                NoteImportSanitizer.PlaceCascade(note, screen, i);
                NoteImportSanitizer.Sanitize(note, screen, i);
                notes.Add(note);
            }
            catch (Exception ex)
            {
                LogHelper.Error($"导入文本文件失败：{path}", ex);
                result.Failures.Add($"「{Path.GetFileName(path)}」：{ex.Message}");
            }
        }

        if (notes.Count > 0)
        {
            var (inserted, skipped) = _noteService.ImportNotes(notes);
            result.Imported = inserted;
            result.Skipped = skipped;
        }

        LogHelper.Info($"导入文本文件：共 {filePaths.Count} 个，导入 {result.Imported}、"
                       + $"跳过 {result.Skipped}、失败 {result.Failures.Count}");
        return result;
    }

    private static Note ToNote(NoteDto dto) => new()
    {
        // Id 缺失或非法时补一个新 Id —— 不能因为一个字段就让整条便签进不来
        Id = dto.Id is { } id && id != Guid.Empty ? id : Guid.NewGuid(),
        Title = dto.Title,
        Content = dto.Content ?? string.Empty,
        // 色名解析永不抛异常：未知颜色兜底为默认色
        Color = NoteColorHelper.Parse(dto.Color),
        PositionX = dto.PositionX ?? 0,
        PositionY = dto.PositionY ?? 0,
        Width = dto.Width ?? 280,
        Height = dto.Height ?? 240,
        Opacity = dto.Opacity ?? 100,
        IsPinned = dto.IsPinned ?? false,
        IsLocked = dto.IsLocked ?? false,
        IsArchived = dto.IsArchived ?? false,
        IsCollapsed = dto.IsCollapsed ?? false,
        IsMinimal = dto.IsMinimal ?? false,
        FontSize = dto.FontSize,
        ZIndex = dto.ZIndex ?? 0,
        CreatedAt = dto.CreatedAt ?? default,
        UpdatedAt = dto.UpdatedAt ?? default,
        DeletedAt = dto.DeletedAt,
    };

    /// <summary>
    /// 导入用的传输对象：字段全部可空，缺字段不等于失败。
    /// 单独定义而不直接反序列化成 <see cref="Note"/>，是为了让导入格式与内部模型解耦 ——
    /// 模型改字段时不会连带把旧导出文件变成不可导入。
    /// </summary>
    private sealed class NoteDto
    {
        public Guid? Id { get; set; }
        public string? Title { get; set; }
        public string? Content { get; set; }
        public string? Color { get; set; }
        public int? PositionX { get; set; }
        public int? PositionY { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? Opacity { get; set; }
        public bool? IsPinned { get; set; }
        public bool? IsLocked { get; set; }
        public bool? IsArchived { get; set; }
        public bool? IsCollapsed { get; set; }
        public bool? IsMinimal { get; set; }
        public double? FontSize { get; set; }
        public int? ZIndex { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }
}
