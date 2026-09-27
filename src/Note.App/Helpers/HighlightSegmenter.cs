namespace NoteApp.Helpers;

/// <summary>一段文本，以及它是否是搜索命中项。</summary>
public readonly record struct HighlightSegment(string Text, bool IsMatch);

/// <summary>
/// 把文本按搜索关键词切成「命中 / 未命中」的片段，供界面做高亮。
///
/// 做成纯函数是为了能完整测试：高亮的边界情况（大小写、多次出现、
/// 关键词为空、关键词比文本长）在界面上很难逐个验证。
/// </summary>
public static class HighlightSegmenter
{
    /// <summary>
    /// 切分文本。关键词为空或文本为空时，只返回一个未命中片段。
    /// 匹配不区分大小写，且**不重叠**地从左到右依次切分。
    /// </summary>
    public static List<HighlightSegment> Split(string? text, string? keyword)
    {
        var value = text ?? string.Empty;
        var segments = new List<HighlightSegment>();

        if (value.Length == 0)
        {
            return segments;
        }

        var target = keyword?.Trim();
        if (string.IsNullOrEmpty(target))
        {
            segments.Add(new HighlightSegment(value, false));
            return segments;
        }

        var start = 0;
        while (start < value.Length)
        {
            var hit = value.IndexOf(target, start, StringComparison.OrdinalIgnoreCase);
            if (hit < 0)
            {
                segments.Add(new HighlightSegment(value[start..], false));
                break;
            }

            if (hit > start)
            {
                segments.Add(new HighlightSegment(value[start..hit], false));
            }

            segments.Add(new HighlightSegment(value.Substring(hit, target.Length), true));
            start = hit + target.Length;
        }

        return segments;
    }

    /// <summary>文本里是否包含关键词（供「是否有命中」之类判断复用同一套比较规则）。</summary>
    public static bool Contains(string? text, string? keyword)
    {
        var target = keyword?.Trim();
        return !string.IsNullOrEmpty(target)
               && (text ?? string.Empty).Contains(target, StringComparison.OrdinalIgnoreCase);
    }
}
