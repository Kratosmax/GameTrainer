using System.Globalization;
using System.Text.Json;

namespace GameTrainer;

/// <summary>
/// 把命令行 / 配置里的原始字符串安全地变成 JS 字面量。
///
/// 背景：jsVal 曾以裸插值进脚本，命令行传
///     jsset 9222 db key "1); evil(("
/// 就能往页面注入任意 JS。修复后的规则只有两条：
///   1. string 类型 → JSON 序列化（自带引号与转义，无法逃逸成代码）；
///   2. 其他类型（number 等）→ 必须能解析成**有限数字**才允许裸插入，
///      否则拒绝（返回 false），不给注入留任何空间。
///
/// 该规则曾只在 AdapterRunner（sem 路径）执行，Program.cs 的老 profile
/// 命令（jsapply / jsset / ApplyInstanceVar）漏了同样的修复——提取成
/// 共享方法后，所有插值点走同一条判定，不再依赖各调用方记得各修一份。
/// </summary>
internal static class JsLiteral
{
    /// <summary>
    /// 尝试把 raw 值转成可安全插入脚本的 JS 字面量。
    /// 返回 false 表示值不合法，调用方必须拒绝写入并说明原因。
    /// </summary>
    public static bool TryPrepare(string rawValue, string valueType, out string jsVal)
    {
        if (valueType == "string")
        {
            jsVal = JsonSerializer.Serialize(rawValue ?? "");
            return true;
        }

        if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
            && !double.IsNaN(num) && !double.IsInfinity(num))
        {
            jsVal = num.ToString("R", CultureInfo.InvariantCulture);
            return true;
        }

        jsVal = null;
        return false;
    }
}
