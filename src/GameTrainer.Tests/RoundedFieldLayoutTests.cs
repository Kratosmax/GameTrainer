using System.Drawing;
using System.Windows.Forms;
using GameTrainer.UI;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 圆角输入框容器必须让子控件垂直居中。
///
/// 为什么需要这条锁：
/// 单行 TextBox 默认 AutoSize = true，高度被字体锁死在 ~23px，
/// 调用方写 Dock = Fill 只能拉宽、拉不高，于是它贴在 38px 高容器的顶部。
/// 实测截图里输入框文字上留白 3px、下留白 18px——视觉上"往上偏 15px"，
/// 而当时所有几何检查（对齐/溢出的断言）都是通过的。
///
/// 这条测试不测像素，只测布局算式：把居中规则钉死，防止有人再把
/// Dock = Fill 或手工 Padding 的写法加回来。
/// </summary>
public class RoundedFieldLayoutTests
{
    private static RoundedField MakeField(int height, int paddingTop = 0)
    {
        var field = new RoundedField
        {
            Size = new Size(320, height),
            Padding = new Padding(Tokens.S3, paddingTop, Tokens.S3, 0)
        };

        var box = new TextBox
        {
            Font = Tokens.FontBody,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill      // 调用方仍可能这么写，容器要能兜住
        };

        field.Controls.Add(box);
        field.PerformLayout();
        return field;
    }

    [Fact]
    public void 单行输入框在容器内垂直居中()
    {
        var field = MakeField(38);
        var box = field.Controls[0];

        int expected = (field.Height - box.Height) / 2;

        Assert.True(Math.Abs(box.Top - expected) <= 1,
            $"输入框 Top={box.Top}，期望 {expected}（容器高 {field.Height}，输入框高 {box.Height}）");
    }

    [Fact]
    public void 子控件的Dock会被容器接管()
    {
        var field = MakeField(38);
        var box = field.Controls[0];

        // 若保留 Dock=Fill，单行 TextBox 会被拉到容器顶部
        Assert.Equal(DockStyle.None, box.Dock);
    }

    [Fact]
    public void 上下内边距不会把居中量推两次()
    {
        // 旧写法靠 Padding.Top 硬凑居中；容器自己居中后，
        // 若仍按 ClientSize 算就会再偏一次（PathBox 的坑）。
        var withPadding = MakeField(38, paddingTop: 6);
        var without = MakeField(38);

        var boxA = withPadding.Controls[0];
        var boxB = without.Controls[0];

        // 视觉位置 = Padding.Top + Top，两者应一致
        int visualA = withPadding.Padding.Top + boxA.Top;
        int visualB = without.Padding.Top + boxB.Top;

        Assert.True(Math.Abs(visualA - visualB) <= 1,
            $"有内边距时视觉 Top={visualA}，无内边距时 {visualB}，应当相同");
    }

    [Fact]
    public void 容器变高后仍然居中()
    {
        var field = MakeField(38);
        var box = field.Controls[0];

        field.Height = 52;
        field.PerformLayout();

        int expected = (field.Height - box.Height) / 2;

        Assert.True(Math.Abs(box.Top - expected) <= 1,
            $"容器变高后 Top={box.Top}，期望 {expected}");
    }
}
