# GameTrainer 接续状态

最后校准：2026-10-21。版本源 `Directory.Build.props` 的 `GameTrainerVersion = 0.1.0`，.NET 8 / WinForms / Windows x64。
本仓库由 `C:\Users\11726\.agents\skills\game-trainer` 的产物迁移而来，**skill 本体仍留在原处**（`~/.agents/skills/game-trainer/SKILL.md`），本仓库只是程序工程，`SKILL.md` 已加入 `.gitignore`。

Git 状态：`main` 分支，已 `git init` 并有本轮提交，**无 remote、无标签**。跨设备接续需先建远端并推送后才成立。

## 接手校准

先读本页、`README.md`、`Directory.Build.props`，再核对：

```powershell
git status --short
Get-Content Directory.Build.props | Select-String GameTrainerVersion
dotnet build GameTrainer.sln -c Release -m:1
```

不要相信本页里没有实际运行过的测试结论。本页记录的都是已执行命令。

## 当前快照（2026-10-21 核验）

| 项 | 状态 |
|---|---|
| 版本 | `0.1.0`，真源 `Directory.Build.props`（实测 `ProductVersion = 0.1.0`） |
| 分支 / 提交 / remote | `main`，本轮有提交，**无 remote** |
| 编译 | `dotnet build GameTrainer.sln -c Release -m:1` → 0 警告 0 错误 |
| UI 截图 | `temp/ui-shot/` 11 张，已逐张查看 |
| 对比度 | `python tools/check-contrast.py` → exit 0，28 项门槛全过，0 失败 |
| 材质 | build 26300 上 `material.applied = True`（Acrylic + 客户区 frame + 系统圆角） |
| 本地预览包 | `temp/preview/GameTrainer-0.1.0-Lite-preview.zip` |
| 业务回归 | 附加 / AOB 唯一 / dry-run / 真实写入 / `--by-profile` 还原，全部实测通过 |
| 线上 | 无。未建 GitHub 仓库、未推送、未发布 |

### 业务回归实测记录（2026-10-21）

靶进程 `GtTestTarget.exe`（PID 16848），未使用任何真实游戏：

```
GameTrainer check gttest        → PID 16848 已附加，区域 461，反作弊未检测，exit 0
GameTrainer aob gttest "<AOB>"  → 命中 1 处 @0x27FDB7BB748，唯一命中
GameTrainer apply gold --dry-run→ 校验通过，1 处 8 字节，未写入，exit 0
GameTrainer apply gold          → 已写入 0x27FDB7BB730（before 00..59 40 → after 00..F0 69 F8 40）
                                  audit.log 记录 action=write, bytes=8
GameTrainer revert gold --by-profile → 按 profile 还原 1 处，99999→100，exit 0
                                  snapshots/active-state.json 已清空为 "Active": []
```

靶进程已用 `Stop-Process` 清理，无残留。


## 本轮做了什么

从 skill 目录迁出的原始代码是 WinForms + 半自绘、无工程骨架。本轮按 `desktop-tool-ui-release` 改造：

1. **工程骨架**：`Directory.Build.props` 作为版本唯一来源并把构建输出重定向到 `temp/`；新增 `GameTrainer.sln`、`.gitignore`、`build/package.ps1`、`tools/check-contrast.py`、`README.md`、本文件。
2. **版本贯通**：`Directory.Build.props` → 程序集 `AssetVersion/FileVersion/InformationalVersion` → `AppVersion.Display` → 设置页「关于」。实测 `ProductVersion = 0.1.0`。
3. **UI 重做**：色值取自 DisplayControlLab `design-review/v3` 截图像素采样（`#3D5DD8` 主色 / `#F5F6FA` 页底 / `#EEF1F6` 侧栏），布局采用「顶栏 + 左侧栏 + 卡片内容区 + 状态栏」。新增 `UI/Paint.cs`、`UI/ProfileList.cs`、`UI/PatchRow.cs`、`UI/RoundedField.cs`。
4. **DWM 材质**：新增 `Native/WindowMaterial.cs`，按 windows-native 基线实现 backdrop + `DwmExtendFrameIntoClientArea` + 圆角，任一步失败撤销 backdrop 并整体回退。
5. **WCAG 核算**：`tools/check-contrast.py` 从 `Tokens.cs` 解析颜色自动验算，禁用态按 WCAG 1.4.3/1.4.11 豁免。

## 本轮修复的真实缺陷（都有截图或日志佐证）

| 缺陷 | 根因 | 修法 |
|---|---|---|
| 卡片标题被正文盖住 | `Dock=Fill` 子控件落在整个客户区 | 重写 `CardPanel.DisplayRectangle` 让出标题高度 |
| 空状态与开关列整块黑底 | `g.Clear(Color.Transparent)` 被解析成 ARGB(0,0,0,0) | `Draw.ClearToParent` 沿父链找第一个不透明色 |
| 窄窗口开关被滚动条压住 | 手工算 `Left/Width` + AutoScroll 组合不可靠 | 改 `Dock=Right` / `Dock=Top` 交给 WinForms 布局 |
| 设置页长句被裁成半行 | `Body` 固定高 26 | 按 `GetPreferredSize` 实测高度 |
| 路径框文字显示不全 | 手工设 `tb.Left/Width` 覆盖了 Dock 结果 | 统一 `Dock=Fill` + host Padding 居中 |
| 包内 `profiles/` 为空 | `Copy-Item -Destination <已存在目录>` 会再套一层 | 清空后按 `*` 展开复制 |
| 开关关闭态对比度 2.98 / 2.84 | `BorderControl #8391A0` 在浅底不足 3:1 | 改 `#7B8998`（白底 3.58 / 页底 3.31 / 侧栏 3.16） |
| 预览包带 `.pdb` | publish 默认输出符号 | 打包脚本删除 `*.pdb` |

## 验证矩阵

```powershell
# 1. 编译（全部四个工程）
dotnet build GameTrainer.sln -c Release -m:1

# 2. UI 真实渲染截图 + 材质返回值
dotnet build src/UiShot/UiShot.csproj -c Release
temp/build/UiShot/Release/net8.0-windows/GtUiShot.exe . temp/ui-shot

# 3. WCAG 对比度核算
python tools/check-contrast.py

# 4. 本地预览包
powershell -File build/package.ps1 -Channel Lite -PreviewOnly

# 5. 包内程序真实可运行
Expand-Archive temp/preview/GameTrainer-0.1.0-Lite-preview.zip temp/preview/verify-lite -Force
temp/preview/verify-lite/GameTrainer.exe profiles
```

注意：`build/package.ps1` 必须保持 **CRLF + UTF-8 BOM**。PowerShell 5.1 解析 LF 换行的中文脚本会报
`Unexpected token '}'`，这不是语法错误。编辑该文件后要重新转 CRLF。

## 不得破坏的约束

- 写入前四道校验（签名唯一 / 前值 / 区间 / 字节上限）不可放宽。
- 检测到反作弊即强制只读，不提供任何绕过、隐藏、伪装路径。
- AOB 签名不得包含会被本次写入改掉的字节。
- `Tokens.cs` 是唯一颜色来源，业务代码不得裸写颜色。
- 版本只改 `Directory.Build.props`，不在 csproj 或代码里另写。
- DWM 材质失败必须整体回退，不允许「标题栏有材质 + 正文不透明」的混合态。
- 提交、推送、打标签、发布需用户单独授权。

## 当前待办

1. 自动更新模块未实现：安装身份、签名清单、下载暂存、包预检、外部替换器。**这是发布前必须补齐的**。
2. 安装器（Inno Setup）未接入，`package.ps1` 目前只产出 ZIP，Full/Lite Setup 缺位。
3. GitHub Actions 工作流未建立。
4. DPI 缺口：当前系统 96 DPI，125% / 150% / 200% 未实测。
5. 深色系统主题未验证（当前仅浅色设计）。
6. 未接真实 3A 游戏长周期测试；Unity/IL2CPP、多线程竞态未验证。

## 维护

每到收尾：更新「最后校准」、快照表、真实跑过的命令与结果、缺陷表新增项、待办增删。删除失效状态，不写逐轮聊天记录。
线上 Release、CI、远端分支未核验时必须明确写「未核验」，不得写成已完成。
