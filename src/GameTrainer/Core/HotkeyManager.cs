using System.Runtime.InteropServices;

namespace GameTrainer.Core;

/// <summary>全局热键。用 RegisterHotKey 而非键盘钩子——不注入、不拦截，系统级注册。</summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyManager(IntPtr hwnd) => _hwnd = hwnd;

    public bool TryRegister(string spec, Action action, out string error)
    {
        error = null;
        if (!TryParse(spec, out uint mods, out uint vk))
        {
            error = $"无法解析热键 \"{spec}\"，格式应如 Ctrl+Alt+F1。";
            return false;
        }

        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk))
        {
            int err = Marshal.GetLastWin32Error();
            error = $"注册热键 {spec} 失败 (Win32 {err})，可能已被其他程序占用。";
            return false;
        }
        _actions[id] = action;
        return true;
    }

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
        if (u.Length >= 2 && u[0] == 'F' && int.TryParse(u[1..], out int fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }
        if (u.Length == 1)
        {
            char c = u[0];
            if (c is >= 'A' and <= 'Z') { vk = c; return true; }
            if (c is >= '0' and <= '9') { vk = c; return true; }
        }
        return false;
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys) UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }
}
