using NoteApp.Helpers;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 搜索高亮切分测试。
///
/// 高亮的边界情况在界面上很难逐个验证（多次出现、大小写、关键词比文本长……），
/// 所以把切分逻辑做成纯函数单独测。它同时被「搜索是否命中」复用，
/// 因此这里的正确性直接决定搜索结果与高亮是否自洽。
/// </summary>
public class HighlightSegmenterTests
{
    [Fact]
    public void 无关键词时应整段未命中()
    {
        var segments = HighlightSegmenter.Split("会议纪要", null);

        var only = Assert.Single(segments);
        Assert.Equal("会议纪要", only.Text);
        Assert.False(only.IsMatch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空白关键词应整段未命中(string keyword)
    {
        var only = Assert.Single(HighlightSegmenter.Split("会议纪要", keyword));

        Assert.False(only.IsMatch);
    }

    [Fact]
    public void 空文本应返回空片段列表()
    {
        Assert.Empty(HighlightSegmenter.Split(null, "x"));
        Assert.Empty(HighlightSegmenter.Split(string.Empty, "x"));
    }

    [Fact]
    public void 命中在中间时切成三段()
    {
        var segments = HighlightSegmenter.Split("明天开产品评审会", "产品");

        Assert.Equal(3, segments.Count);
        Assert.Equal(("明天开", false), (segments[0].Text, segments[0].IsMatch));
        Assert.Equal(("产品", true), (segments[1].Text, segments[1].IsMatch));
        Assert.Equal(("评审会", false), (segments[2].Text, segments[2].IsMatch));
    }

    [Fact]
    public void 命中在开头时不应产生空前缀段()
    {
        var segments = HighlightSegmenter.Split("产品评审会", "产品");

        Assert.Equal(2, segments.Count);
        Assert.True(segments[0].IsMatch);
        Assert.Equal("产品", segments[0].Text);
        Assert.Equal("评审会", segments[1].Text);
    }

    [Fact]
    public void 命中在结尾时不应产生空后缀段()
    {
        var segments = HighlightSegmenter.Split("明天开产品", "产品");

        Assert.Equal(2, segments.Count);
        Assert.Equal("明天开", segments[0].Text);
        Assert.True(segments[1].IsMatch);
        Assert.Equal("产品", segments[1].Text);
    }

    [Fact]
    public void 整段都是关键词时应只有一个命中段()
    {
        var only = Assert.Single(HighlightSegmenter.Split("产品", "产品"));

        Assert.True(only.IsMatch);
    }

    [Fact]
    public void 多次出现应逐段命中()
    {
        var segments = HighlightSegmenter.Split("a-b-a-b-a", "a");

        Assert.Equal(new[] { true, false, true, false, true },
            segments.Select(s => s.IsMatch).ToArray());
        Assert.Equal("a-b-a-b-a", string.Concat(segments.Select(s => s.Text)));
    }

    [Fact]
    public void 相邻重复出现不应重叠()
    {
        // "aaaa" 里找 "aa"：应切成两段各 "aa"，而不是三段
        var segments = HighlightSegmenter.Split("aaaa", "aa");

        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.True(s.IsMatch));
        Assert.Equal("aaaa", string.Concat(segments.Select(s => s.Text)));
    }

    [Fact]
    public void 匹配应忽略大小写()
    {
        var segments = HighlightSegmenter.Split("Hello World", "world");

        Assert.Contains(segments, s => s.IsMatch && s.Text == "World");
    }

    [Fact]
    public void 未命中时只返回一段原文()
    {
        var only = Assert.Single(HighlightSegmenter.Split("会议纪要", "产品"));

        Assert.False(only.IsMatch);
        Assert.Equal("会议纪要", only.Text);
    }

    [Fact]
    public void 关键词比文本长时应视为未命中()
    {
        var only = Assert.Single(HighlightSegmenter.Split("短", "很长的关键词"));

        Assert.False(only.IsMatch);
    }

    [Fact]
    public void 切分结果拼回来必须等于原文()
    {
        // 任何情况下都不能丢字或多字 —— 这是高亮最不能出的错
        foreach (var (text, keyword) in new[]
                 {
                     ("明天开产品评审会，产品负责人到场", "产品"),
                     ("a", "a"),
                     ("", "x"),
                     ("重复重复重复", "重复"),
                     ("特殊字符 - [ ] 待办", "[ ]"),
                 })
        {
            var joined = string.Concat(HighlightSegmenter.Split(text, keyword).Select(s => s.Text));
            Assert.Equal(text, joined);
        }
    }

    [Theory]
    [InlineData("明天开产品评审会", "产品", true)]
    [InlineData("Hello World", "world", true)]
    [InlineData("会议纪要", "产品", false)]
    [InlineData("会议纪要", "", false)]
    [InlineData("会议纪要", null, false)]
    [InlineData(null, "产品", false)]
    public void 命中判断应与切分规则一致(string? text, string? keyword, bool expected)
    {
        Assert.Equal(expected, HighlightSegmenter.Contains(text, keyword));

        var hasMatch = HighlightSegmenter.Split(text, keyword).Any(s => s.IsMatch);
        Assert.Equal(expected, hasMatch);
    }
}
