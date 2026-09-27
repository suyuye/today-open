using NoteApp.ViewModels;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 便签行解析测试。
///
/// 这块最值得用测试守住：待办语法是**存在纯文本内容里**的（<c>- [ ] </c> / <c>- [x] </c>），
/// 一旦解析规则改错，用户既有的待办会全部退化成普通文本行 —— 而界面看起来
/// 只是「复选框不见了」，很难联想到是解析问题。
/// </summary>
public class NoteLineViewModelTests
{
    private static NoteLineViewModel Parse(string line) => NoteLineViewModel.Parse(new RecordingLineParent(), line);

    [Fact]
    public void 未完成待办_应解析为待办行并去掉前缀()
    {
        var line = Parse("- [ ] 买牛奶");

        Assert.Equal(NoteLineType.Todo, line.LineType);
        Assert.Equal("买牛奶", line.Text);
        Assert.False(line.IsChecked);
    }

    [Fact]
    public void 已完成待办_小写x_应解析为已完成()
    {
        var line = Parse("- [x] 写周报");

        Assert.Equal(NoteLineType.Todo, line.LineType);
        Assert.Equal("写周报", line.Text);
        Assert.True(line.IsChecked);
    }

    [Fact]
    public void 已完成待办_大写X_应解析为已完成()
    {
        var line = Parse("- [X] 大写X也应识别");

        Assert.Equal(NoteLineType.Todo, line.LineType);
        Assert.True(line.IsChecked);
    }

    [Theory]
    [InlineData("- [ ]")]
    [InlineData("- [x]")]
    [InlineData("- [X]")]
    public void 无内容待办_无尾随空格也应识别(string raw)
    {
        var line = Parse(raw);

        Assert.Equal(NoteLineType.Todo, line.LineType);
        Assert.Equal(string.Empty, line.Text);
    }

    /// <summary>
    /// 缺前导连接号的写法**不是**待办。
    /// 这是实际踩过的坑：手工造测试数据时写成 `[ ] xxx`，
    /// 结果复选框不出现，一度被误判成界面 Bug。
    /// </summary>
    [Theory]
    [InlineData("[ ] 缺连接号")]
    [InlineData("[x] 缺连接号")]
    [InlineData("- 普通列表项")]
    [InlineData("正文里出现 - [ ] 但不合法")]
    [InlineData("")]
    [InlineData("   ")]
    public void 非待办写法_应保持普通文本行(string raw)
    {
        var line = Parse(raw);

        Assert.Equal(NoteLineType.Normal, line.LineType);
        Assert.Equal(raw, line.Text);
    }

    [Theory]
    [InlineData("- [ ] 买牛奶", "- [ ] 买牛奶")]
    [InlineData("- [x] 写周报", "- [x] 写周报")]
    [InlineData("- [X] 大写X", "- [x] 大写X")]
    public void 待办行_转回纯文本的规范形式(string raw, string expected)
    {
        // 勾选标记统一输出小写 x
        Assert.Equal(expected, Parse(raw).ToPlainText());
    }

    [Fact]
    public void 无内容待办_标记后固定保留一个空格()
    {
        // 这是既有行为（标记与内容之间固定一个空格，内容为空时末尾即空格）。
        // 特意写成测试记录下来，避免今后误当 Bug 顺手改掉 ——
        // 那会改变已存内容的格式，属于数据格式变更而非无害的整理。
        Assert.Equal("- [ ] ", Parse("- [ ]").ToPlainText());
        Assert.Equal("- [x] ", Parse("- [x]").ToPlainText());
    }

    [Theory]
    [InlineData("- [ ] 买牛奶")]
    [InlineData("- [x] 写周报")]
    [InlineData("- [X] 大写X")]
    [InlineData("- [ ]")]
    [InlineData("- [x]")]
    public void 待办行_往返应稳定且不丢信息(string raw)
    {
        var first = Parse(raw);
        var stored = first.ToPlainText();
        var second = NoteLineViewModel.Parse(new RecordingLineParent(), stored);

        Assert.Equal(first.LineType, second.LineType);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(first.IsChecked, second.IsChecked);
        // 第二次转换不得再改变任何东西（幂等）
        Assert.Equal(stored, second.ToPlainText());
    }

    [Fact]
    public void 普通行_转回纯文本应原样返回()
    {
        Assert.Equal("就是一段普通文字", Parse("就是一段普通文字").ToPlainText());
    }

    [Fact]
    public void 勾选待办_应同时通知待办状态与内容变化()
    {
        // 前者刷新折叠态徽标，后者触发自动保存：两者职责不同，都必须发生
        var parent = new RecordingLineParent();
        var line = NoteLineViewModel.Parse(parent, "- [ ] 待办");

        line.IsChecked = true;

        Assert.Equal(1, parent.TodoStateChangedCount);
        Assert.Equal(1, parent.ContentChangedCount);
    }

    [Fact]
    public void 勾选普通行_不应触发任何通知()
    {
        var parent = new RecordingLineParent();
        var line = NoteLineViewModel.Parse(parent, "普通文字");

        line.IsChecked = true;

        Assert.Equal(0, parent.TodoStateChangedCount);
        Assert.Equal(0, parent.ContentChangedCount);
    }

    [Fact]
    public void 修改行文本_应通知内容变化()
    {
        var parent = new RecordingLineParent();
        var line = NoteLineViewModel.Parse(parent, "原文字");

        line.Text = "改过的文字";

        Assert.Equal(1, parent.ContentChangedCount);
    }

    [Fact]
    public void 待办完成后取消勾选_也应通知内容变化()
    {
        var parent = new RecordingLineParent();
        var line = NoteLineViewModel.Parse(parent, "- [x] 已完成");

        line.IsChecked = false;

        Assert.Equal(1, parent.TodoStateChangedCount);
        Assert.Equal(1, parent.ContentChangedCount);
        Assert.Equal("- [ ] 已完成", line.ToPlainText());
    }
}
