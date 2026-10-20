#!/usr/bin/env python3
"""WCAG 对比度核算。把 Tokens.cs 里的语义色逐对验算，不靠肉眼。

规则（WCAG 2.1）：
  正文文字  >= 4.5:1
  大号文字（>=18.66px bold 或 24px）>= 3.0:1
  UI 组件边界 / 图形对象 >= 3.0:1

输出 JSON 供交付引用；任一不达标即返回非零退出码。
"""
import json, re, sys, pathlib

TOKENS = pathlib.Path(r"F:\git\temp\game-trainer\src\GameTrainer\UI\Tokens.cs")
OUT = pathlib.Path(r"F:\git\temp\game-trainer\temp\validation\contrast.json")

src = TOKENS.read_text(encoding="utf-8")
pattern = re.compile(r"public static readonly Color (\w+)\s*=\s*Color\.FromArgb\(0x([0-9A-Fa-f]{2}),\s*0x([0-9A-Fa-f]{2}),\s*0x([0-9A-Fa-f]{2})\)")
colors = {m.group(1): (int(m.group(2), 16), int(m.group(3), 16), int(m.group(4), 16)) for m in pattern.finditer(src)}

def lum(rgb):
    def ch(v):
        s = v / 255.0
        return s / 12.92 if s <= 0.03928 else ((s + 0.055) / 1.055) ** 2.4
    r, g, b = rgb
    return 0.2126 * ch(r) + 0.7152 * ch(g) + 0.0722 * ch(b)

def contrast(fg, bg):
    a, b = lum(fg), lum(bg)
    hi, lo = max(a, b), min(a, b)
    return (hi + 0.05) / (lo + 0.05)

def hexs(rgb):
    return "#%02X%02X%02X" % rgb

# (前景, 背景, 最低要求, 用途说明)
CHECKS = [
    ("TextPrimary",   "Surface",        4.5, "卡片正文/标题 · 白底"),
    ("TextPrimary",   "PageBack",       4.5, "页面正文 · 浅灰底"),
    ("TextPrimary",   "SidebarBack",    4.5, "侧栏正文"),
    ("TextSecondary", "Surface",        4.5, "次要文字 · 白底"),
    ("TextSecondary", "PageBack",       4.5, "次要文字 · 浅灰底"),
    ("TextSecondary", "SidebarBack",    4.5, "侧栏分组标题"),
    ("TextMuted",     "Surface",        4.5, "空状态说明 · 白底"),
    ("Accent",        "AccentSubtle",   4.5, "选中导航项文字"),
    ("Accent",        "Surface",        4.5, "链接/动作文字"),
    ("Accent",        "PageBack",       4.5, "页面内链接"),
    ("TextOnAccent",  "Accent",         4.5, "主按钮文字"),
    ("TextOnAccent",  "AccentHover",    4.5, "主按钮悬停文字"),
    ("TextOnAccent",  "AccentPressed",  4.5, "主按钮按下文字"),
    ("TextOnAccent",  "Danger",         4.5, "危险按钮文字"),
    ("TextOnAccent",  "DangerHover",    4.5, "危险按钮悬停文字"),
    ("TextOnAccent",  "DangerPressed",  4.5, "危险按钮按下文字"),
    ("Success",       "SuccessSubtle",  4.5, "成功徽标"),
    ("Success",       "Surface",        4.5, "成功状态文字"),
    ("Danger",        "DangerSubtle",   4.5, "阻断徽标"),
    ("Danger",        "Surface",        4.5, "危险说明文字"),
    ("Warning",       "Surface",        4.5, "警告状态文字"),
    ("Warning",       "WarningSubtle",  4.5, "警告徽标"),

    # UI 组件边界：>= 3.0
    ("BorderControl", "Surface",        3.0, "开关轨道(关)/输入框边框"),
    ("BorderControl", "SurfaceSunken",  3.0, "开关轨道 · 浅底"),
    ("BorderControl", "PageBack",       3.0, "边框 · 页面底色"),
    ("BorderControl", "SidebarBack",    3.0, "边框 · 侧栏底色"),
    ("Accent",        "Surface",        3.0, "开关轨道(开)/焦点框"),
    ("TextSecondary", "Surface",        3.0, "选中行左侧强调条"),
]

# 禁用态。WCAG 2.1 1.4.3 / 1.4.11 明确豁免禁用组件，
# 因此只记录实测值、不参与门槛判定，也不允许据此把颜色改成更浅。
EXEMPT = [
    ("TextDisabled", "Surface", "禁用文字/开关轨道（WCAG 豁免）"),
]

rows, failures = [], []
for fg, bg, need, label in CHECKS:
    if fg not in colors or bg not in colors:
        failures.append({"check": label, "error": f"缺少颜色定义: {fg}/{bg}"})
        continue
    ratio = contrast(colors[fg], colors[bg])
    ok = ratio >= need
    row = {
        "label": label,
        "foreground": hexs(colors[fg]), "background": hexs(colors[bg]),
        "ratio": round(ratio, 2), "required": need, "pass": ok,
    }
    rows.append(row)
    if not ok:
        failures.append(row)

OUT.parent.mkdir(parents=True, exist_ok=True)
exempt_rows = []
for fg, bg, label in EXEMPT:
    if fg not in colors or bg not in colors:
        continue
    exempt_rows.append({
        "label": label,
        "foreground": hexs(colors[fg]), "background": hexs(colors[bg]),
        "ratio": round(contrast(colors[fg], colors[bg]), 2),
        "required": None,
        "note": "WCAG 2.1 1.4.3/1.4.11 豁免禁用组件，不参与门槛",
    })

OUT.write_text(json.dumps({
    "generatedAt": "2026-10-21", "source": str(TOKENS),
    "tokensParsed": len(colors),
    "checks": rows, "exempt": exempt_rows, "failures": failures,
}, ensure_ascii=False, indent=2), encoding="utf-8")

print(f"颜色数 = {len(colors)}, 门槛检查 = {len(rows)}, 不达标 = {len(failures)}")
for r in rows:
    flag = "PASS" if r["pass"] else "FAIL"
    print(f"  [{flag}] {r['label']:34s} {r['foreground']} on {r['background']}  "
          f"{r['ratio']:5.2f} (>= {r['required']})")
for r in exempt_rows:
    print(f"  [EXEMPT] {r['label']:32s} {r['foreground']} on {r['background']}  {r['ratio']:5.2f}")
sys.exit(1 if failures else 0)
