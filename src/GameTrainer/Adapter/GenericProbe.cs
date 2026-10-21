using System.Text.Json;

namespace GameTrainer.Adapter;

/// <summary>
/// 通用 JS 对象探测：用于 Electron / 纯 NW.js / 其他 Web 游戏。
///
/// 与 Construct 3 探测的根本差别：Construct 有统一的运行时 API
/// （GetAllObjectClasses / GetInstanceVariableValue），
/// 而 Electron 游戏**没有标准对象模型**——每个游戏把状态挂在不同地方。
///
/// 所以这里用启发式：从若干常见根（window、全局游戏对象、常见框架命名空间）
/// 出发，广度遍历有限深度，找出「名字像血量/金币且值是数字」的属性。
///
/// 设计约束：
///   - 必须限制深度与节点数，否则大游戏的全局对象图会遍历到卡死。
///   - 只读，绝不调用任何可能改变状态的函数。
///   - 找不到就如实报告，不编造绑定。
/// </summary>
internal static class GenericProbe
{
    /// <summary>常见游戏框架的全局命名空间，优先尝试。</summary>
    private static readonly string[] KnownRoots =
    {
        "window.game", "window.Game", "window.player", "window.Player",
        "window.state", "window.gameState", "window.GameState",
        "window.save", "window.saveData", "window.data",
        "Phaser", "Phaser.Game", "PIXI", "Cocos", "cc", "Laya",
        "Unity", "Godot", "engine", "app", "GameGlobal"
    };

    /// <summary>
    /// 探测对象模型。
    /// 返回找到的候选：对象路径 + 变量名 + 当前值 + 语义猜测。
    /// </summary>
    public static List<Result> Probe(CdpClient cdp, Action<string> progress)
    {
        var results = new List<Result>();

        string script = $$"""
(() => {
  const KNOWN_ROOTS = {{JsonSerializer.Serialize(KnownRoots)}};
  const MAX_NODES = 4000;
  const MAX_DEPTH = 3;
  const out = { roots: [], hits: [], scanned: 0, truncated: false };

  // 只读探测：绝不调用函数，只读取数据属性
  const seen = new WeakSet();
  let nodes = 0;

  function isPlain(v) {
    if (v === null || typeof v !== 'object') return false;
    const t = Object.prototype.toString.call(v);
    return t === '[object Object]' || Array.isArray(v);
  }

  function readValue(v) {
    const t = typeof v;
    if (t === 'number') return Number.isFinite(v) ? v : String(v);
    if (t === 'string') return v.length <= 40 ? v : v.slice(0, 40) + '…';
    if (t === 'boolean') return v;
    return null;   // 对象/函数不展开
  }

  // 语义关键词：只用于打分，实际映射交给 C# 的 Semantics.Guess
  function looksInteresting(name) {
    const n = String(name).toLowerCase();
    if (n.length < 2 || n.length > 32) return false;
    // 排除明显不是数值状态的
    if (/^(on|handle|callback|listener|prototype|constructor|__)/.test(n)) return false;
    return true;
  }

  function walk(obj, path, depth) {
    if (nodes >= MAX_NODES) { out.truncated = true; return; }
    if (depth > MAX_DEPTH) return;
    if (!isPlain(obj) || seen.has(obj)) return;
    seen.add(obj);

    let keys;
    try { keys = Object.keys(obj); } catch (e) { return; }
    if (!keys) return;
    // 宽度限制只对**子对象**生效：根对象（window）天然有几百个键，
    // 一律跳过会导致一个节点都进不去（实测 scanned=0 的根因）。
    if (depth > 0 && keys.length > 200) return;

    for (const k of keys) {
      if (nodes >= MAX_NODES) { out.truncated = true; return; }
      nodes++;

      let v;
      try { v = obj[k]; } catch (e) { continue; }   // getter 可能抛异常

      const leaf = readValue(v);
      if (leaf !== null && looksInteresting(k)) {
        out.hits.push({ path: path + '.' + k, name: k, value: leaf });
        continue;
      }

      if (isPlain(v)) walk(v, path + '.' + k, depth + 1);
    }
  }

  // 1) 先探已知根
  for (const r of KNOWN_ROOTS) {
    let v;
    try {
      v = r.split('.').reduce((o, k) => (o == null ? o : o[k]), self);
    } catch (e) { continue; }
    if (v == null) continue;
    out.roots.push(r);
    walk(v, r, 0);
  }

  // 2) 再扫 window 上自定义的顶层对象（框架没在已知列表里时靠这条）
  try {
    const winKeys = Object.keys(self);
    for (const k of winKeys) {
      if (nodes >= MAX_NODES) { out.truncated = true; break; }
      // 跳过浏览器内置与已处理的
      if (/^(webkit|chrome|document|location|navigator|history|screen|performance|console|localStorage|sessionStorage|indexedDB|caches|crypto|top|parent|self|frames|opener|closed|length|name|status|origin|external|speechSynthesis|customElements|visualViewport|scheduler|trustedTypes|crossOriginIsolated|isSecureContext|devicePixelRatio|innerWidth|innerHeight|outerWidth|outerHeight|scrollX|scrollY|frameElement|event|clientInformation|styleMedia|menubar|toolbar|statusbar|personalbar|scrollbars|locationbar|directories|on)/.test(k)) continue;
      let v;
      try { v = self[k]; } catch (e) { continue; }
      if (v === null || typeof v !== 'object') continue;
      if (v === self || v === window) continue;
      walk(v, k, 0);
    }
  } catch (e) {}

  out.scanned = nodes;
  return JSON.stringify(out);
})()
""";

        string json;
        try
        {
            json = cdp.Evaluate(script, timeoutMs: 45000);
        }
        catch (Exception ex)
        {
            progress($"      探测脚本执行失败：{ex.Message}");
            return results;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("roots", out var roots) && roots.GetArrayLength() > 0)
        {
            var names = new List<string>();
            foreach (var x in roots.EnumerateArray()) names.Add(x.GetString());
            progress($"      识别到已知根: {string.Join(", ", names)}");
        }
        else
        {
            progress("      未识别到已知框架根，改用全局对象扫描");
        }

        if (root.TryGetProperty("scanned", out var sc))
            progress($"      遍历节点 {sc.GetInt32()} 个");

        if (root.TryGetProperty("truncated", out var tr) && tr.GetBoolean())
            progress("      注意：达到遍历上限，结果可能不完整");

        if (!root.TryGetProperty("hits", out var hits))
            return results;

        foreach (var h in hits.EnumerateArray())
        {
            string path = h.GetProperty("path").GetString();
            string name = h.GetProperty("name").GetString();
            var valEl = h.GetProperty("value");
            string value = valEl.ValueKind == JsonValueKind.String
                ? valEl.GetString()
                : valEl.GetRawText();

            var (semantic, score) = Semantics.Guess(name);
            if (score < 0.5) continue;   // 只保留有意义的候选

            results.Add(new Result(path, name, value, semantic, score));
        }

        return results;
    }

    public sealed record Result(string Path, string Name, string Value, string Semantic, double Score);
}
