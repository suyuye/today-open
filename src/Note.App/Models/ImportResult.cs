namespace NoteApp.Models;

/// <summary>导入结果（用于向用户如实汇报：进来了多少、跳过了多少、哪几条没成功）。</summary>
public class ImportResult
{
    /// <summary>实际导入的条数。</summary>
    public int Imported { get; set; }

    /// <summary>因已存在相同 Id 而跳过的条数。</summary>
    public int Skipped { get; set; }

    /// <summary>失败项的人类可读说明（文件级或条目级）。</summary>
    public List<string> Failures { get; } = new();

    public bool HasFailures => Failures.Count > 0;

    /// <summary>一句话总结，可直接用于提示。</summary>
    public string Summary
    {
        get
        {
            var text = Imported > 0 ? $"已导入 {Imported} 条" : "没有导入任何便签";

            if (Skipped > 0)
            {
                text += $"，跳过 {Skipped} 条（已存在相同便签）";
            }

            if (Failures.Count > 0)
            {
                text += $"，{Failures.Count} 项失败";
            }

            return text + "。";
        }
    }
}
