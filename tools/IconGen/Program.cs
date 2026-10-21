using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace GameTrainer.IconGen;

/// <summary>
/// 生成 GameTrainer 图标（多尺寸 .ico）。
///
/// 设计对齐 GUI 内的品牌方标：Accent 色（#3D5DD8）圆角方底 + 白色 "G"。
/// 为什么用代码画而不是找现成图：配色必须与 Tokens.Accent 严格一致，
/// 手绘或多工具导出容易出现色差；代码生成可重跑、可审。
///
/// 尺寸覆盖 Windows 各场景：
///   16  —— 托盘、任务栏小图标
///   24  —— 列表视图
///   32  —— 桌面快捷方式
///   48  —— 中等图标
///   64  —— 大图标
///   256 —— 超大图标 / 商店展示
/// </summary>
internal static class Program
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 256 };

    // 与 Tokens.Accent 同值（#3D5DD8）
    private static readonly Color Accent = Color.FromArgb(0x3D, 0x5D, 0xD8);

    private static int Main(string[] args)
    {
        string outPath = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "gametrainer.ico");

        try
        {
            var frames = new List<(int Size, byte[] Png)>();
            foreach (int size in Sizes)
                frames.Add((size, RenderPng(size)));

            WriteIco(outPath, frames);
            Console.WriteLine($"已生成 {outPath}");
            Console.WriteLine($"尺寸: {string.Join(", ", Sizes)}");
            Console.WriteLine($"字节: {new FileInfo(outPath).Length:N0}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[失败] " + ex.Message);
            return 1;
        }
    }

    /// <summary>渲染单个尺寸为 PNG 字节。</summary>
    private static byte[] RenderPng(int size)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // 圆角半径：小尺寸按比例缩小，否则 16px 会被圆角吃掉几乎全部面积
            float radius = Math.Max(2f, size * 0.22f);
            float inset = size <= 16 ? 0f : size * 0.02f;
            var rect = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);

            using (var path = RoundedRect(rect, radius))
            using (var brush = new SolidBrush(Accent))
                g.FillPath(brush, path);

            // 白色 G：字号按尺寸调，16px 下的细节靠加粗而不是缩放
            float fontSize = size * 0.62f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            // 视觉居中：字母的基线让 G 偏上，往下挪一点
            var textRect = new RectangleF(0, size * 0.02f, size, size);
            g.DrawString("G", font, textBrush, textRect, format);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// 写 ICO 容器。Vista 以后的 ICO 支持内嵌 PNG，
    /// 直接存 PNG 帧比存 BMP 更小且支持 alpha。
    /// </summary>
    private static void WriteIco(string path, List<(int Size, byte[] Png)> frames)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);

        // ICONDIR
        w.Write((ushort)0);              // reserved
        w.Write((ushort)1);              // type = icon
        w.Write((ushort)frames.Count);   // count

        // 目录项后紧跟图像数据
        int offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size));   // 256 记作 0
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);        // 调色板色数
            w.Write((byte)0);        // reserved
            w.Write((ushort)1);      // planes
            w.Write((ushort)32);     // bpp
            w.Write(png.Length);     // 数据字节数
            w.Write(offset);         // 数据偏移
            offset += png.Length;
        }

        foreach (var (_, png) in frames)
            w.Write(png);
    }
}
