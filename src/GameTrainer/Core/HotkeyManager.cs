using System.Runtime.InteropServices;

namespace GameTrainer.Core;

/// <summary>
/// 全局热键。用 RegisterHotKey 而非键盘钩子——不注入、不拦截，系统级注册。
///
/// 与旧版的区别：
///   - 公开 TryParseSpec 供设置弹窗做保存前校验（避免存了却注册不上）
///   - 支持按 key 注销单个热键（改绑时不必重建全部）
///   - 支持 RegisterAll 批量注册并在任一失败时整体回滚
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _actions = new();
    private readonly Dictionary<string, int> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId = 1;

    public HotkeyManager(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>已成功注册的热键数量。</summary>
    public int Count => _byKey.Count;

    /// <summary>
    /// 校验并解析热键字符串，不实际注册。
    /// 设置弹窗在保存前调用，避免"存进配置文件但系统注册失败"。
    /// </summary>
    public static bool TryParseSpec(string spec, out uint mods, out uint vk, out string error)
    {
        error = null;
        mods = 0; vk = 0;

        if (string.IsNullOrWhiteSpace(spec))
        {
            error = "快捷键为空";
            return false;
        }

        if (!TryParse(spec, out mods, out vk))
        {
            error = $"无法解析 \"{spec}\"，示例：Ctrl+Alt+F1、Ctrl+Shift+G";
            return false;
        }

        // 无修饰键的组合会抢占其它程序的普通按键
        if (mods == 0)
        {
            error = "必须包含 Ctrl / Alt / Shift / Win 中至少一个修饰键";
            return false;
        }

        return true;
    }

    /// <summary>注册一个热键。key 用于后续改绑或注销。</summary>
    public bool TryRegister(string key, string spec, Action action, out string error)
    {
        error = null;

        if (!TryParseSpec(spec, out uint mods, out uint vk, out error))
            return false;

        // 同一个 key 重复注册时先注销旧的
        if (_byKey.TryGetValue(key, out int oldId))
        {
            UnregisterHotKey(_hwnd, oldId);
            _actions.Remove(oldId);
            _byKey.Remove(key);
        }

        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk))
        {
            int err = Marshal.GetLastWin32Error();
            error = err == 1409
                ? $"热键 {spec} 已被其他程序占用"
                : $"注册热键 {spec} 失败（Win32 {err}）";
            return false;
        }

        _actions[id] = action;
        _byKey[key] = id;
        return true;
    }

    /// <summary>
    /// 批量注册。任一条失败就注销本批已成功的，保持"要么全有、要么全无"。
    /// 这样用户不会看到"一半热键生效、一半不生效"的中间态。
    /// </summary>
    public bool RegisterAll(
        IEnumerable<(string Key, string Spec, Action Action)> items,
        out List<string> errors)
    {
        errors = new List<string>();
        var registered = new List<string>();

        foreach (var (key, spec, action) in items)
        {
            if (string.IsNullOrWhiteSpace(spec)) continue;

            if (TryRegister(key, spec, action, out string err))
            {
                registered.Add(key);
            }
            else
            {
                errors.Add(err);
            }
        }

        if (errors.Count > 0)
        {
            foreach (string key in registered) Unregister(key);
            errors.Add("已回滚本次全部热键注册");
            return false;
        }
        return true;
    }

    /// <summary>注销单个热键。</summary>
    public void Unregister(string key)
    {
        if (!_byKey.TryGetValue(key, out int id)) return;
        UnregisterHotKey(_hwnd, id);
        _actions.Remove(id);
        _byKey.Remove(key);
    }

    /// <summary>当前已注册的热键规格，用于界面显示。</summary>
    public IReadOnlyDictionary<string, int> RegisteredKeys => _byKey;

    public void HandleMessage(int msg, IntPtr wParam)
    {
        if (msg != WM_HOTKEY) return;
        if (_actions.TryGetValue(wParam.ToInt32(), out var a)) a();
    }

    private static bool TryParse(string spec, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => p.Trim()).ToArray();
        if (parts.Length == 0) return false;

        foreach (string p in parts)
        {
            switch (p.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": mods |= MOD_CONTROL; break;
                case "ALT": mods |= MOD_ALT; break;
                case "SHIFT": mods |= MOD_SHIFT; break;
                case "WIN": mods |= MOD_WIN; break;
                default:
                    if (!TryParseKey(p, out vk)) return false;
                    break;
            }
        }
        return vk != 0;
    }

    private static bool TryParseKey(string k, out uint vk)
    {
        vk = 0;
        string u = k.ToUpperInvariant();

        // F1..F24
        if (u.Length >= 2 && u[0] == 'F' && int.TryParse(u[1..], out int fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        // 小键盘 0-9
        if (u.StartsWith("NUM", StringComparison.Ordinal) &&
            int.TryParse(u[3..], out int num) && num is >= 0 and <= 9)
        {
            vk = (uint)(0x60 + num);
            return true;
        }

        // A-Z / 0-9
        if (u.Length == 1)
        {
            char c = u[0];
            if (c is >= 'A' and <= 'Z') { vk = c; return true; }
            if (c is >= '0' and <= '9') { vk = c; return true; }
        }

        // 常用命名键
        vk = u switch
        {
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            _ => 0
        };
        return vk != 0;
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys.ToList())
            UnregisterHotKey(_hwnd, id);
        _actions.Clear();
        _byKey.Clear();
    }
}
