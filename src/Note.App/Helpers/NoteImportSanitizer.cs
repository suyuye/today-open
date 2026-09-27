using System.IO;
using System.Windows;

using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Models.Enums;

namespace NoteApp.Helpers;

/// <summary>
/// 导入便签时对外部数据做安全整理。
///
/// 为什么单独抽出来并做成纯函数：导入的数据来自**应用之外**（用户手改的 JSON、
/// 别的软件导出的 Markdown），字段可能缺、类型可能怪、坐标可能指向早已不存在的显示器。
/// 其中最要紧的是坐标 —— 一条落在屏幕外的便签，用户**既看不到也拖不回来**，
/// 表象和「导入失败」完全一样，排查起来非常费劲。所以这里宁可重新摆放，
/// 也不原样信任外部坐标。
///
/// 不依赖数据库与 UI，因此可以完整地写单元测试。
/// </summary>
public static class NoteImportSanitizer
{
    /// <summary>便签最小可编辑尺寸，防止外部数据给出无法操作的极小尺寸。</summary>
    public const int MinWidth = 180;
    public const int MinHeight = 120;

    /// <summary>标题栏至少要有这么多像素落在屏幕内，才拖得回来。</summary>
    public const int MinGrabWidth = 80;
    public const int MinGrabHeight = 12;

    /// <summary>标题栏高度（判断「能不能抓住」时只看这一条）。</summary>
    private const int TitleBarHeight = 40;

    /// <summary>重新摆放时的起始留白与层叠步长。</summary>
    private const int CascadeMargin = 40;
    private const int CascadeStep = 28;

    /// <summary>
    /// 把一条外部便签整理成安全可用的值。
    /// </summary>
    /// <param name="note">待整理的便签（就地修改）。</param>
    /// <param name="screen">可用屏幕范围（通常是虚拟屏，含多显示器）。</param>
    /// <param name="cascadeIndex">用于重新摆放时的层叠序号。</param>
    public static void Sanitize(Note note, Rect screen, int cascadeIndex)
    {
        note.Title = string.IsNullOrWhiteSpace(note.Title) ? null : note.Title.Trim();
        note.Content ??= string.Empty;

        // 尺寸：既不能小到没法用，也不能大到超出屏幕
        var maxWidth = Math.Max(MinWidth, (int)screen.Width - 20);
        var maxHeight = Math.Max(MinHeight, (int)screen.Height - 20);
        note.Width = Math.Clamp(note.Width, MinWidth, maxWidth);
        note.Height = Math.Clamp(note.Height, MinHeight, maxHeight);

        note.Opacity = Math.Clamp(note.Opacity, 30, 100);

        if (note.UpdatedAt == default)
        {
            note.UpdatedAt = DateTime.Now;
        }

        if (note.CreatedAt == default)
        {
            note.CreatedAt = note.UpdatedAt;
        }

        if (!IsReachable(note, screen))
        {
            PlaceCascade(note, screen, cascadeIndex);
        }
    }

    /// <summary>
    /// 便签是否还有可抓取的部分落在屏幕内。
    /// 只看标题栏那一条：只要标题栏还能被点到，用户就能把它拖回视野中央。
    /// </summary>
    public static bool IsReachable(Note note, Rect screen)
    {
        var titleBar = new Rect(note.PositionX, note.PositionY, note.Width, TitleBarHeight);
        var visible = Rect.Intersect(titleBar, screen);

        return !visible.IsEmpty
               && visible.Width >= MinGrabWidth
               && visible.Height >= MinGrabHeight;
    }

    /// <summary>
    /// 把便签层叠摆放到屏幕内（导入的便签常常没有可靠坐标）。
    /// 先从上往下排，排满一列再往右开新列，保证任何数量都不会跑出屏幕。
    /// </summary>
    public static void PlaceCascade(Note note, Rect screen, int index)
    {
        if (index < 0)
        {
            index = 0;
        }

        var usableWidth = Math.Max(1, (int)screen.Width - note.Width - CascadeMargin * 2);
        var usableHeight = Math.Max(1, (int)screen.Height - note.Height - CascadeMargin * 2);

        var rowsPerColumn = Math.Max(1, usableHeight / CascadeStep);
        var maxColumns = Math.Max(1, usableWidth / (CascadeStep * 2) + 1);

        var column = (index / rowsPerColumn) % maxColumns;
        var row = index % rowsPerColumn;

        note.PositionX = (int)screen.Left + CascadeMargin + column * CascadeStep * 2;
        note.PositionY = (int)screen.Top + CascadeMargin + row * CascadeStep;
    }

    /// <summary>
    /// 把一个 Markdown / 纯文本文件整理成一条便签。
    /// 标题优先取文件首行的 Markdown 一级标题（并把它从正文里去掉），否则用文件名。
    /// 正文保留原文（含 <c>- [ ]</c> 待办语法），只统一换行符。
    /// </summary>
    public static Note FromTextFile(string filePath, string fileText, NoteColor color)
    {
        var content = (fileText ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var title = Path.GetFileNameWithoutExtension(filePath);
        var lines = content.Split('\n');

        if (lines.Length > 0 && lines[0].StartsWith("# ", StringComparison.Ordinal))
        {
            var heading = lines[0][2..].Trim();
            if (heading.Length > 0)
            {
                title = heading;
            }

            content = string.Join('\n', lines.Skip(1)).TrimStart('\n');
        }

        return new Note
        {
            Title = title,
            Content = content.TrimEnd(),
            Color = color,
            Opacity = 100,
            Width = 280,
            Height = 240,
        };
    }
}
