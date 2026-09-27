using NoteApp.Models;
using NoteApp.Models.Enums;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 便签呈现形态（折叠 / 极简）收口测试。
///
/// 背景：这两个形态以前是两个独立 bool，互斥关系靠两处手写 if 维持，
/// 所有派生视觉又都要同时读这两个字段各自组合 —— 组合爆炸，漏一处就是边界 bug。
/// 现在形态是单一枚举 <see cref="NoteDisplayMode"/>，两个 bool 降级为
/// **派生视图**（只读），这些测试就是锁住这个不变量的。
/// </summary>
public class NoteDisplayModeTests
{
    // ===== Note.DisplayMode 的映射 =====

    [Fact]
    public void 默认形态应为常规()
    {
        Assert.Equal(NoteDisplayMode.Normal, new Note().DisplayMode);
    }

    [Fact]
    public void 默认形态不应是紧凑形态()
    {
        Assert.False(new Note().IsCompactDisplay);
    }

    [Theory]
    [InlineData(false, false, NoteDisplayMode.Normal)]
    [InlineData(true, false, NoteDisplayMode.Collapsed)]
    [InlineData(false, true, NoteDisplayMode.Minimal)]
    public void 两个旧布尔列应正确映射为形态(bool collapsed, bool minimal, NoteDisplayMode expected)
    {
        var note = new Note { IsCollapsed = collapsed, IsMinimal = minimal };

        Assert.Equal(expected, note.DisplayMode);
    }

    /// <summary>
    /// 外部数据可能两列同时为 true（手改的 JSON、旧版本写下的库）。
    /// 必须归并成一个确定性结果，且**按折叠处理** —— 折叠的窗口高度是固定值，
    /// 若当成极简会得到一个高度错误的窗口。
    /// </summary>
    [Fact]
    public void 两列同时为真时应归并为折叠()
    {
        var note = new Note { IsCollapsed = true, IsMinimal = true };

        Assert.Equal(NoteDisplayMode.Collapsed, note.DisplayMode);
    }

    /// <summary>
    /// 非法组合（两列同为 true）在**读取**时被归并，而不是在字段上被改写 ——
    /// 未经 setter 的 Note 实例，两个 bool 仍是原样，但 DisplayMode 已经是唯一确定的。
    /// 这保证了"读取形态"永远安全，同时不强求外部数据立刻被纠正。
    /// </summary>
    [Fact]
    public void 非法组合读取时应归并为唯一形态()
    {
        var note = new Note { IsCollapsed = true, IsMinimal = true };

        // 读取入口只给一个确定答案
        Assert.Equal(NoteDisplayMode.Collapsed, note.DisplayMode);
        // 且它不可能是"两个形态同时成立"
        Assert.NotEqual(NoteDisplayMode.Minimal, note.DisplayMode);
    }

    /// <summary>
    /// 一旦经由 setter 写入形态，非法组合就被真正消除 —— 这是应用内正常的写入路径。
    /// </summary>
    [Fact]
    public void 写入形态应消除非法组合()
    {
        var note = new Note { IsCollapsed = true, IsMinimal = true };

        note.DisplayMode = note.DisplayMode;   // 走一次 setter

        Assert.True(note.IsCollapsed);
        Assert.False(note.IsMinimal);
    }

    // ===== 写回时两个旧列必须同步 =====

    [Theory]
    [InlineData(NoteDisplayMode.Normal, false, false)]
    [InlineData(NoteDisplayMode.Collapsed, true, false)]
    [InlineData(NoteDisplayMode.Minimal, false, true)]
    public void 写入形态应同步两个旧列(NoteDisplayMode mode, bool expectCollapsed, bool expectMinimal)
    {
        var note = new Note();

        note.DisplayMode = mode;

        Assert.Equal(expectCollapsed, note.IsCollapsed);
        Assert.Equal(expectMinimal, note.IsMinimal);
    }

    /// <summary>
    /// 反复在形态间切换后，两个旧列必须始终与形态一致 ——
    /// 不能出现"切走时忘了清掉另一个"的残留（这正是收口前手写 if 的风险）。
    /// </summary>
    [Fact]
    public void 连续切换形态不应留下残留()
    {
        var note = new Note();

        foreach (var mode in new[]
                 {
                     NoteDisplayMode.Collapsed, NoteDisplayMode.Minimal,
                     NoteDisplayMode.Normal, NoteDisplayMode.Minimal,
                     NoteDisplayMode.Collapsed, NoteDisplayMode.Normal,
                 })
        {
            note.DisplayMode = mode;
            Assert.Equal(mode, note.DisplayMode);
        }
    }

    // ===== IsCompactDisplay =====

    [Theory]
    [InlineData(NoteDisplayMode.Normal, false)]
    [InlineData(NoteDisplayMode.Collapsed, true)]
    [InlineData(NoteDisplayMode.Minimal, true)]
    public void 紧凑形态判定应覆盖折叠与极简(NoteDisplayMode mode, bool expected)
    {
        var note = new Note { DisplayMode = mode };

        Assert.Equal(expected, note.IsCompactDisplay);
    }

    // ===== 枚举取值护栏 =====

    /// <summary>
    /// 存储层靠这两个布尔列落库，枚举的取值本身**不进数据库**。
    /// 若有人给枚举重排数值，虽然当前映射不依赖数值，但序列化/比较类代码可能受影响，
    /// 这里锁住取值，避免无声变更。
    /// </summary>
    [Fact]
    public void 形态枚举取值应保持稳定()
    {
        Assert.Equal(0, (int)NoteDisplayMode.Normal);
        Assert.Equal(1, (int)NoteDisplayMode.Collapsed);
        Assert.Equal(2, (int)NoteDisplayMode.Minimal);
    }

    [Fact]
    public void 形态枚举应恰好三种()
    {
        Assert.Equal(3, System.Enum.GetValues<NoteDisplayMode>().Length);
    }
}
