using NoteApp.Models;
using NoteApp.Models.Enums;
using NoteApp.ViewModels;

using Xunit;

namespace NoteApp.Tests;

/// <summary>列表项 ViewModel 的显示派生与命令转发测试。</summary>
public class NoteItemViewModelTests
{
    private static Tag Tag(int id, string name) =>
        new() { Id = id, Name = name, Color = NoteColor.GrayDeep, CreatedAt = new DateTime(2026, 1, 1).AddMinutes(id) };

    private static NoteItemViewModel Item(Note note, RecordingNoteService service, params Tag[] tags) =>
        new(note, service, tags);

    [Fact]
    public void 有标题时应直接用标题()
    {
        var note = new Note { Title = "会议纪要", Content = "正文第一行" };
        var item = Item(note, TestDoubles.Service());

        Assert.Equal("会议纪要", item.Title);
    }

    [Fact]
    public void 标题为空时应取内容首行前20字()
    {
        var note = new Note { Title = null, Content = new string('字', 30) + "\n第二行" };
        var item = Item(note, TestDoubles.Service());

        Assert.Equal(new string('字', 20), item.Title);
        Assert.Equal(20, item.Title.Length);
    }

    [Fact]
    public void 标题为空白时应取内容首行()
    {
        var note = new Note { Title = "   ", Content = "实际标题\n第二行" };
        var item = Item(note, TestDoubles.Service());

        Assert.Equal("实际标题", item.Title);
    }

    [Fact]
    public void 预览文字应压平换行并截断到80字()
    {
        var note = new Note { Content = "第一行\r\n第二行\n" + new string('字', 100) };
        var item = Item(note, TestDoubles.Service());

        Assert.DoesNotContain('\n', item.PreviewText);
        Assert.DoesNotContain('\r', item.PreviewText);
        Assert.Equal(80, item.PreviewText.Length);
    }

    [Fact]
    public void 预览文字_短内容应原样保留()
    {
        var note = new Note { Content = "很短" };
        var item = Item(note, TestDoubles.Service());

        Assert.Equal("很短", item.PreviewText);
    }

    [Fact]
    public void 无标签时标签文本为空()
    {
        var note = new Note();
        var item = Item(note, TestDoubles.Service());

        Assert.Equal(string.Empty, item.TagText);
    }

    [Theory]
    [InlineData(1, " · 甲")]
    [InlineData(2, " · 甲 +1")]
    [InlineData(3, " · 甲 +2")]
    public void 标签文本_首个标签加其余数量(int count, string expected)
    {
        var note = new Note();
        var tags = Enumerable.Range(1, count).Select(i => Tag(i, i == 1 ? "甲" : $"标签{i}")).ToArray();
        var item = Item(note, TestDoubles.Service(), tags);

        Assert.Equal(expected, item.TagText);
    }

    [Fact]
    public void 状态属性应反映便签()
    {
        var note = new Note { IsPinned = true, IsLocked = true, IsArchived = true, DeletedAt = DateTime.Now };
        var item = Item(note, TestDoubles.Service());

        Assert.True(item.IsPinned);
        Assert.True(item.IsLocked);
        Assert.True(item.IsArchived);
        Assert.True(item.IsInTrash);
        Assert.False(item.IsNormal);
    }

    [Fact]
    public void 未删除便签应为Normal()
    {
        var item = Item(new Note(), TestDoubles.Service());

        Assert.True(item.IsNormal);
        Assert.False(item.IsInTrash);
    }

    [Fact]
    public void 提醒时间应格式化为年月日时分()
    {
        var at = new DateTime(2026, 12, 1, 9, 30, 0);
        var item = new NoteItemViewModel(new Note(), TestDoubles.Service(), Array.Empty<Tag>(), at);

        Assert.Equal("2026-12-01 09:30", item.ReminderTimeText);
        Assert.True(item.HasReminderTime);
    }

    [Fact]
    public void 无提醒时提醒文本为空且标记为假()
    {
        var item = Item(new Note(), TestDoubles.Service());

        Assert.Equal(string.Empty, item.ReminderTimeText);
        Assert.False(item.HasReminderTime);
    }

    [Fact]
    public void 就地刷新后_标签与提醒显示应更新()
    {
        var note = new Note { Title = "标题", Content = "内容" };
        var item = Item(note, TestDoubles.Service());

        var changed = new List<string>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        item.ApplySnapshot(new[] { Tag(1, "新标签") }, new DateTime(2027, 1, 2, 3, 4, 0));

        Assert.Equal(" · 新标签", item.TagText);
        Assert.Equal("2027-01-02 03:04", item.ReminderTimeText);
        Assert.True(item.HasReminderTime);
        Assert.Contains(nameof(NoteItemViewModel.TagText), changed);
        Assert.Contains(nameof(NoteItemViewModel.ReminderTimeText), changed);
    }

    [Fact]
    public void 就地刷新_相同内容不应重复通知()
    {
        var note = new Note();
        var at = new DateTime(2027, 1, 2, 3, 4, 0);
        var item = new NoteItemViewModel(note, TestDoubles.Service(), Array.Empty<Tag>(), at);

        var count = 0;
        item.PropertyChanged += (_, _) => count++;

        item.ApplySnapshot(Array.Empty<Tag>(), at);

        Assert.Equal(0, count);
    }

    [Fact]
    public void 底层字段变化后_应通知全部派生显示属性()
    {
        var note = new Note { Title = "旧标题", Content = "旧内容" };
        var item = Item(note, TestDoubles.Service());

        var changed = new List<string>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        note.Title = "新标题";
        note.IsPinned = true;
        item.NotifyNoteFieldsChanged();

        Assert.Equal("新标题", item.Title);
        Assert.True(item.IsPinned);
        Assert.Contains(nameof(NoteItemViewModel.Title), changed);
        Assert.Contains(nameof(NoteItemViewModel.IsPinned), changed);
        Assert.Contains(nameof(NoteItemViewModel.PreviewText), changed);
        Assert.Contains(nameof(NoteItemViewModel.RelativeUpdateTime), changed);
    }

    [Fact]
    public void 列表项应与传入的便签共享同一实例()
    {
        // 列表刷新时就地改写该实例的字段，因此不能是副本 ——
        // 否则选中项与右侧编辑区持有的引用会失配
        var note = new Note { Title = "甲" };
        var item = Item(note, TestDoubles.Service());

        Assert.Same(note, item.Note);
    }

    [Fact]
    public void 置顶命令应转发到服务并带上便签Id()
    {
        var note = new Note();
        var service = TestDoubles.Service();
        var item = Item(note, service);

        item.TogglePinCommand.Execute(null);

        Assert.Equal($"TogglePin:{note.Id}", Assert.Single(service.Calls));
    }

    [Fact]
    public void 锁定与归档命令应转发到服务()
    {
        var note = new Note();
        var service = TestDoubles.Service();
        var item = Item(note, service);

        item.ToggleLockCommand.Execute(null);
        item.ToggleArchiveCommand.Execute(null);

        Assert.Equal(new[] { $"ToggleLock:{note.Id}", $"ToggleArchive:{note.Id}" }, service.Calls);
    }

    [Fact]
    public void 删除与恢复命令应转发到服务()
    {
        var note = new Note();
        var service = TestDoubles.Service();
        var item = Item(note, service);

        item.DeleteCommand.Execute(null);
        item.RestoreCommand.Execute(null);

        Assert.Equal(new[] { $"Delete:{note.Id}", $"Restore:{note.Id}" }, service.Calls);
    }
}
