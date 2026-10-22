namespace GameTrainer.UI;

/// <summary>
/// 缓动函数集合。
///
/// 参照 iOS 的 CAMediaTimingFunction：UI 动效基本只用四条曲线——
/// 默认 ease-in-out、进入 ease-out、退出 ease-in、强调用带轻微过冲的曲线。
/// 不要自己发明曲线，iOS 的"自然"来自克制的位移幅度 + 恰当的曲线 + 稳定帧率。
///
/// 所有函数输入输出都是 0..1，调用方负责插值实际数值。
/// </summary>
internal static class Easing
{
    /// <summary>iOS 默认曲线（等价 UIViewAnimationCurveEaseInOut），最常用于状态切换。</summary>
    public static float EaseInOut(float t) => CubicBezier(t, 0.42f, 0f, 0.58f, 1f);

    /// <summary>进入曲线：起步快、收尾缓，用于元素出现。</summary>
    public static float EaseOut(float t) => CubicBezier(t, 0f, 0f, 0.58f, 1f);

    /// <summary>退出曲线：起步缓、收尾快，用于元素消失。</summary>
    public static float EaseIn(float t) => CubicBezier(t, 0.42f, 0f, 1f, 1f);

    /// <summary>
    /// 轻微过冲曲线（OriginOS 弹性手感）。
    ///
    /// 过冲峰值约 5%，比原来 1.7 系数的 10% 更克制——
    /// 过冲大了元素会"跳一下"，显得廉价而不是有弹性。
    ///
    /// 重要：返回值可能 > 1（峰值 1.05）。
    /// 所有用它做插值的调用方都必须自己夹范围，
    /// 否则 Color.FromArgb 收到 alpha=268 会直接抛异常（已踩过）。
    /// </summary>
    public static float SpringOut(float t)
    {
        if (t >= 1f) return 1f;
        const float c = 1.3f;
        float u = t - 1f;
        return 1f + (c + 1f) * u * u * u + c * u * u;
    }

    /// <summary>
    /// 三次贝塞尔求值。参数与 CSS cubic-bezier() 相同。
    /// 用牛顿迭代反解 x 再取 y——这是浏览器和 iOS 的通行做法。
    /// </summary>
    public static float CubicBezier(float t, float x1, float y1, float x2, float y2)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;

        float x = SolveForX(t, x1, x2);
        return BezierY(x, y1, y2);
    }

    private static float SolveForX(float targetX, float x1, float x2)
    {
        float x = targetX;
        for (int i = 0; i < 8; i++)
        {
            float cx = BezierX(x, x1, x2) - targetX;
            if (Math.Abs(cx) < 1e-5f) break;

            float d = BezierDX(x, x1, x2);
            if (Math.Abs(d) < 1e-6f) break;
            x -= cx / d;
            x = Math.Clamp(x, 0f, 1f);
        }
        return x;
    }

    private static float BezierX(float t, float x1, float x2)
        => 3f * (1 - t) * (1 - t) * t * x1 + 3f * (1 - t) * t * t * x2 + t * t * t;

    private static float BezierY(float t, float y1, float y2)
        => 3f * (1 - t) * (1 - t) * t * y1 + 3f * (1 - t) * t * t * y2 + t * t * t;

    private static float BezierDX(float t, float x1, float x2)
    {
        float u = 1 - t;
        return 3f * u * u * x1 + 6f * u * t * (x2 - x1) + 3f * t * t * (1 - x2);
    }

    /// <summary>数值插值。t 会夹到 0..1，避免过冲曲线把值推出去。</summary>
    public static float Lerp(float a, float b, float t)
        => a + (b - a) * Math.Clamp(t, 0f, 1f);

    public static int LerpInt(int a, int b, float t)
        => (int)Math.Round(a + (b - a) * Math.Clamp(t, 0f, 1f));

    /// <summary>
    /// 颜色插值。t 与每个通道都夹范围。
    ///
    /// 必须夹：SpringOut 过冲时 t 可达 1.05，
    /// 不夹的话 Color.FromArgb 收到 268 会抛 ArgumentException，
    /// 整个绘制崩掉（实测踩过一次，弹窗报 "Value of 261 is not valid for alpha"）。
    /// </summary>
    public static Color LerpColor(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            Math.Clamp((int)(a.A + (b.A - a.A) * t), 0, 255),
            Math.Clamp((int)(a.R + (b.R - a.R) * t), 0, 255),
            Math.Clamp((int)(a.G + (b.G - a.G) * t), 0, 255),
            Math.Clamp((int)(a.B + (b.B - a.B) * t), 0, 255));
    }

    /// <summary>
    /// 给颜色套一个透明度。alpha 夹到 0..255。
    /// 淡入淡出统一走这里，避免各处手算 alpha 溢出。
    /// </summary>
    public static Color WithAlpha(Color c, float alpha01)
        => Color.FromArgb(Math.Clamp((int)(255 * alpha01), 0, 255), c.R, c.G, c.B);
}
