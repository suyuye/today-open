using NoteApp.Helpers;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 图片缩放尺寸测试。
///
/// 存库前统一等比缩小是控制数据库（以及每份备份）体积的关键一步。
/// 这步算错很难发现：把竖图当横图算会得到畸形比例，
/// 对本来很小的图"缩放"反而会放大变糊。所以用测试把规则钉死。
/// </summary>
public class ImageSizingTests
{
    [Fact]
    public void 小于上限的图片应原样返回且不放大()
    {
        var (w, h) = ImageSizing.Fit(800, 600);

        Assert.Equal(800, w);
        Assert.Equal(600, h);
    }

    [Fact]
    public void 正好等于上限应保持不变()
    {
        var (w, h) = ImageSizing.Fit(ImageSizing.MaxDimension, 900);

        Assert.Equal(ImageSizing.MaxDimension, w);
        Assert.Equal(900, h);
    }

    [Fact]
    public void 横图应按最长边缩到上限并保持比例()
    {
        var (w, h) = ImageSizing.Fit(3200, 1600);

        Assert.Equal(1600, w);
        Assert.Equal(800, h);
    }

    [Fact]
    public void 竖图也应按最长边缩放()
    {
        // 最容易被写错的一种：只看宽度会把竖图漏掉
        var (w, h) = ImageSizing.Fit(900, 3600);

        Assert.Equal(400, w);
        Assert.Equal(1600, h);
    }

    [Fact]
    public void 正方形应两边等长()
    {
        var (w, h) = ImageSizing.Fit(4000, 4000);

        Assert.Equal(1600, w);
        Assert.Equal(1600, h);
    }

    [Fact]
    public void 极端长条图缩放后仍应至少为1像素()
    {
        // 不能因为取整得出 0，否则会生成非法尺寸的位图
        var (w, h) = ImageSizing.Fit(20000, 3);

        Assert.True(w >= 1);
        Assert.True(h >= 1);
        Assert.Equal(1600, w);
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(-100, 600)]
    [InlineData(600, 0)]
    [InlineData(0, 0)]
    public void 非法尺寸应退回安全值而不抛异常(int width, int height)
    {
        var (w, h) = ImageSizing.Fit(width, height);

        Assert.True(w >= 1);
        Assert.True(h >= 1);
    }

    [Fact]
    public void 上限非正数时应不做缩放()
    {
        var (w, h) = ImageSizing.Fit(1000, 500, 0);

        Assert.Equal(1000, w);
        Assert.Equal(500, h);
    }

    [Fact]
    public void 缩放后比例应与原图基本一致()
    {
        var (w, h) = ImageSizing.Fit(4032, 3024);   // 4:3 手机照片

        Assert.Equal(1600, w);
        // 允许 1 像素的取整误差
        Assert.InRange(h, 1200 - 1, 1200 + 1);
    }

    [Theory]
    [InlineData(800, 600, false)]
    [InlineData(1600, 900, false)]
    [InlineData(1601, 900, true)]
    [InlineData(900, 1601, true)]
    [InlineData(0, 100, false)]
    public void 是否需要缩放(int width, int height, bool expected)
    {
        Assert.Equal(expected, ImageSizing.NeedsResize(width, height));
    }

    [Fact]
    public void 默认上限应为1600()
    {
        Assert.Equal(1600, ImageSizing.MaxDimension);
    }
}
