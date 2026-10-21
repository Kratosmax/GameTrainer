#!/usr/bin/env python3
"""对照 desktop-tool-ui-release/ui-design.md 审查 GameTrainer 主窗口。

审查的是**控件定义 + 布局参数**，不是审美偏好。
每条给出：规范条款 → 现状 → 是否达标。

用法: python tools/review-ui.py
输出: temp/validation/ui-review.md
"""
import re
import pathlib

ROOT = pathlib.Path(r"F:\git\temp\game-trainer\src\GameTrainer")
OUT = pathlib.Path(r"F:\git\temp\game-trainer\temp\validation\ui-review.md")

gui = (ROOT / "GuiForm.cs").read_text(encoding="utf-8")
card = (ROOT / "UI/ValueCard.cs").read_text(encoding="utf-8")
tokens = (ROOT / "UI/Tokens.cs").read_text(encoding="utf-8")

rows = []


def add(item, clause, status, evidence):
    rows.append({"item": item, "clause": clause, "status": status, "evidence": evidence})


# ---- 1. 窗口尺寸 ----
m = re.search(r"MinimumSize\s*=\s*new Size\((\d+),\s*(\d+)\)", gui)
min_ok = m and int(m.group(1)) >= 900 and int(m.group(2)) >= 640
add("最小窗口尺寸", "合理的 min size，内容可伸缩",
    "OK" if min_ok else "需改", f"MinimumSize = {m.group(0) if m else '未找到'}")

m = re.search(r"(?<!Minimum)Size\s*=\s*new Size\((\d+),\s*(\d+)\)", gui)
add("默认窗口尺寸", "首屏能容纳核心内容",
    "OK" if m and int(m.group(1)) >= 1200 else "需改",
    f"Size = {m.group(0) if m else '未找到'}")

# ---- 2. 布局稳定性 ----
add("卡片行高固定", "动态状态不得改变行高",
    "OK" if "Math.Max(y + Tokens.S3, 168)" in card else "需改",
    "ValueCard.Relayout 固定最小高度 168" if "168" in card else "未找到固定高度")

add("按钮固定高度", "按钮尺寸稳定，文字居中",
    "OK" if "Height = Tokens.ControlHeight" in (ROOT / "UI/FlatControls.cs").read_text(encoding="utf-8") else "需改",
    "FlatButton 构造期设 Height = Tokens.ControlHeight")

add("文本垂直居中", "单行控件文字上下居中",
    "OK" if "TextFormatFlags.VerticalCenter" in (ROOT / "UI/Paint.cs").read_text(encoding="utf-8") else "需改",
    "Draw.Text 统一使用 VerticalCenter")

# ---- 3. 长文本 ----
has_ellipsis = "EndEllipsis" in (ROOT / "UI/Paint.cs").read_text(encoding="utf-8")
add("长文本尾部省略", "超长内容提供省略",
    "OK" if has_ellipsis else "需改", "Draw.Text 默认 EndEllipsis=True")

has_tip = "_tip.SetToolTip" in card or "ToolTip" in gui
add("长文本悬停提示", "提供悬停完整提示",
    "部分" if has_tip else "缺失",
    "ValueCard 有 ToolTip 字段但未对 hint 文本挂载；ProfileRow 已挂载")

# ---- 4. 折叠 ----
add("低频操作折叠", "信息密度高时提供折叠",
    "OK" if "_fold" in card else "需改",
    "ValueCard 的「手动设置」默认折叠，展开后纵向生长")

# ---- 5. 空状态 ----
add("空状态占位", "不留纯空白",
    "OK" if "_patchEmpty" in gui and "Heading" in gui else "需改",
    "EmptyState 显示说明文字")

# ---- 6. 错误反馈 ----
add("错误可见", "失败要明确反馈",
    "OK" if "_hint.ForeColor = Tokens.Danger" in card else "需改",
    "ValueCard 读取失败/超范围时 hint 转 danger 色")

# ---- 7. 禁用态 ----
add("禁用态区分", "禁用不能只靠透明度到不可读",
    "OK" if "Tokens.TextDisabled" in (ROOT / "UI/FlatControls.cs").read_text(encoding="utf-8") else "需改",
    "FlatButton 禁用态用 SurfaceSunken 底 + TextDisabled 前景")

# ---- 8. 危险动作确认 ----
add("危险动作确认", "不可逆操要有确认路径",
    "需改", "「回满」「应用」直接生效，无确认；对血量类可接受，但金币无上限确认")

# ---- 9. 日志区 ----
add("日志区可折叠", "低频区占主要空间时应可收起",
    "需改" if "logCard" in gui and "Collapse" not in gui else "OK",
    "日志卡片固定占右侧 42%，无法收起")

# ---- 10. 多游戏切换 ----
add("侧栏显示引擎与值数", "对象列表要能识别",
    "OK" if "Bindings.Count" in gui and "Engine" in gui else "需改",
    "侧栏副标题 = 引擎 + 数值个数")

# ---- 11. 刷新反馈 ----
add("刷新连击保护", "长任务期间禁用冲突操作",
    "缺失", "RefreshValuesAsync 无防重入，快速连点会并发连接")

# ---- 12. 状态栏 ----
add("底部状态栏信息", "状态可理解",
    "部分", "状态栏仍是旧 profile 语义（附加/反作弊/自启），未反映适配包连接状态")

# ---- 输出 ----
OUT.parent.mkdir(parents=True, exist_ok=True)
lines = ["# GameTrainer 主窗口 UI 审查", "",
         "对照 `desktop-tool-ui-release/references/ui-design.md` 逐条核对。", ""]
lines.append("| 项目 | 规范条款 | 结论 | 证据 |")
lines.append("|---|---|---|---|")
for r in rows:
    lines.append(f"| {r['item']} | {r['clause']} | **{r['status']}** | {r['evidence']} |")

need = [r for r in rows if r["status"] != "OK"]
lines += ["", f"## 需处理（{len(need)} 项）", ""]
for r in need:
    lines.append(f"- **{r['item']}**（{r['status']}）：{r['evidence']}")

OUT.write_text("\n".join(lines), encoding="utf-8")
print("\n".join(lines))
