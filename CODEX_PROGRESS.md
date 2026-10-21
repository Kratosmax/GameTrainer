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

## 当前快照（2026-10-21 晚间核验）

| 项 | 状态 |
|---|---|
| 版本 | `1.0.0`，真源 `Directory.Build.props` |
| 分支 / 提交 / remote | `main`，见 `git log`；remote 见 `git remote -v` |
| 编译 | `dotnet build GameTrainer.sln -c Release -m:1` → 0 警告 0 错误 |
| 单元测试 | `dotnet test src/GameTrainer.Tests` → **225 全过** |
| 对比度 | `python tools/check-contrast.py` → exit 0 |
| UI 审查 | `python tools/review-ui.py` → exit 0 |
| 焦点态截图 | `temp/ui-focus2/` 14 张（程序内打开 ForceFocusCue 后自截，不注入按键） |
| 启动方式 | `Exe`（控制台子系统）+ 无参数时 `ShowWindow(SW_HIDE)` 隐藏黑框 |
| 网络 | GitHub DNS/HTTPS/git 协议/API 全部可达，`gh` 已认证 `Kratosmax` |

### 本轮（2026-10-21 晚间）修复清单

六个提交，全部经双向代码评审（人 + 另一 agent 交叉审）：

| 提交 | 内容 |
|---|---|
| `af00f97` | M1：`CdpClient.Evaluate` 实例级串行锁，消除并发竞态 |
| `a0f43b9` | 打包：数据目录劫持修复 + 排除自测靶 profile |
| `86ada52` | M5 连接复用 / M7 dry-run 假值 / A1 UI 冻结 / A2 刷新按钮失联 / A4 托盘气泡 / E1 崩溃路径 / V1 持久勾选清零 / V2 文案 |
| `85ce8ec` | V4 键盘焦点可见（ForceFocusCue + UiShot 焦点态截图）/ V6 静默失败改可见反馈 |
| `3fc89cb` | ShowNoValue 幂等化（连点累积后缀是真 bug）/ ShowBusy 标注不可达 |
| `f10c954` | V3 悬停提示落地（挂 hint + warning，按 MeasureText 判截断）/ 删除 DoAttach 死路径 |

### 本轮修复的真实缺陷（都有实测证据）

| 缺陷 | 证据 |
|---|---|
| 写内存不校验区域类型，可覆盖代码段 | chain 指向模块+0x1000 时被拒：「位于可执行映像（代码段）」；堆上 100→4321 不受影响 |
| 命令行可注入任意 JS | `sem write gold "1); evil(("` → 拒绝；投毒适配包 Read 路径也拦（marker 检查 `__PWNED__` 未设置） |
| PID 复用窗口可写错进程 | 写句柄上二次 `GetProcessTimes` 校验 |
| 日志写失败谎报写入结果 | 两处日志降级吞掉；`Enable` 那处原先会触发全量回滚 |
| 反作弊注册表误报 | 改查 SCM 服务运行态（`QueryServiceStatus`）+ Start 值 |
| AuditLog 锁失效 | 实例级锁改 `static`（MemoryBackend 每次 new 实例） |
| 数据目录劫持 | 包解压在工程内时 `profiles` 命令列出仓库的 gttest；修复后显示「还没有任何 profile」 |
| dry-run 显示旧值 | 修后输出 `100 → 7777`（目标值），读回仍 100（未写入） |
| GUI 启动冻结 30s | `await Task.Run(() => GameLauncher.Launch(...))` |
| 刷新按钮永远禁用 | 拆出独立 `_refreshBtn`，`Kind = Secondary` |
| 持久勾选瞬间清零护盾 | `ValueCard.cs:166` `if (_busy || !_isOn) return;` |
| ToggleSwitch 无焦点框 | 补绘制；且修了 `inflate(track)` 越界导致底部被裁 |

### 未验证（诚实缺口）

- **真实 Unity / UE 游戏**：`memory_aob` 只用自建测试靶验证过，无商业游戏样本
- **Tab 遍历顺序**：需人工按一次确认（注入按键会抢使用者焦点）
- **深色系统主题**、**125%/150% DPI**：当前仅 96 DPI 浅色验证
- **真实反作弊环境**：只读切换的代码路径存在，未在装反作弊的机器上跑过
- **自动更新链路**：未实现（无 `update.json`、无更新器）

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

### 干净重建复核（2026-10-21 00:14）

删掉 `temp/build`、`temp/obj`、`temp/ui-shot`、`temp/preview` 后从提交状态重跑，全部通过：

```
dotnet build GameTrainer.sln -c Release -m:1        → 0 警告 0 错误
GtUiShot.exe . temp/ui-shot                          → 9 张截图，material.applied = True
python tools/check-contrast.py                       → exit 0，checks=28 failures=0 exempt=1
build/package.ps1 -Channel Lite -PreviewOnly         → 142,980 字节
```

预览包哈希与上一轮不同（142,981 → 142,980）属正常：ZIP 内含构建时间戳，
每次构建必然不同。核验时以当次 `SHA256SUMS.txt` 为准，不要跨轮比对哈希。


## 本轮做了什么

从 skill 目录迁出的原始代码是 WinForms + 半自绘、无工程骨架。本轮按 `desktop-tool-ui-release` 改造：

1. **工程骨架**：`Directory.Build.props` 作为版本唯一来源并把构建输出重定向到 `temp/`；新增 `GameTrainer.sln`、`.gitignore`、`build/package.ps1`、`tools/check-contrast.py`、`README.md`、本文件。
2. **版本贯通**：`Directory.Build.props` → 程序集 `AssetVersion/FileVersion/InformationalVersion` → `AppVersion.Display` → 设置页「关于」。实测 `ProductVersion = 0.1.0`。
3. **UI 重做**：色值取自 DisplayControlLab `design-review/v3` 截图像素采样（`#3D5DD8` 主色 / `#F5F6FA` 页底 / `#EEF1F6` 侧栏），布局采用「顶栏 + 左侧栏 + 卡片内容区 + 状态栏」。新增 `UI/Paint.cs`、`UI/ProfileList.cs`、`UI/PatchRow.cs`、`UI/RoundedField.cs`。
4. **DWM 材质**：新增 `Native/WindowMaterial.cs`，只保留「标题栏 Acrylic + 系统圆角」，**不做全客户区 frame 扩展**（原因见下方缺陷表「整窗发白」一行）。
5. **WCAG 核算**：`tools/check-contrast.py` 从 `Tokens.cs` 解析颜色自动验算，禁用态按 WCAG 1.4.3/1.4.11 豁免。
6. **启动方式**：程序改为 `WinExe`（GUI 子系统），无参数双击直接开窗口；命令行模式通过 `AttachConsole(ATTACH_PARENT_PROCESS)` 保留 stdout。

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
| **双击秒闪退** | `OutputType=Exe`（控制台子系统）+ 无参数时 `PrintHelp(); return 0` | 改 `WinExe`；无参数直接 `GuiForm.Run`；命令行经 `AttachConsole` 保留输出 |
| **整窗发白、文字看不清** | `DwmExtendFrameIntoClientArea` 全客户区扩展。WinForms 无透明 composition target，DWM 在不透明绘制之上再混 Acrylic，正文对比度塌陷 | 取消客户区扩展，只保留标题栏材质 + 系统圆角 |

### 关于全窗毛玻璃的能力边界

`ui-design.md` 描述的是 WPF 路径：`AllowsTransparency` / 透明 composition target + DWM frame 扩展。
WinForms 用 GDI 绘制不透明客户区，直接套同一套调用会得到「整窗发白」，实测证据在
`temp/ui-shot/live-run.png`（修复前）与 `live-run3.png`（修复后）。

要真正实现 WinForms 全窗毛玻璃，需要改用 layered window 或换框架，不是调参能解决的。
当前选择是保证可读性优先：材质只作用于标题栏。

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

## 启动与状态（方案 A：消除调试端口摩擦）

对应 skill 第零节「问题 4」的方案 A。核心思想：**发现手段和运行依赖不该是同一个东西**，
但至少先把"每次要记 --remote-debugging-port"这个摩擦消掉。

```
launch <gameId> [--no-wait]   读适配包 → 定位 exe → 带端口启动 → 等就绪
status <gameId>               进程 / 端口 / 绑定 三项状态
probe <port> --game <gameId>  探测前自动启动游戏
sem <gameId> ...              读取/写入前自动启动游戏
```

**方案 A 的完整形态**：`sem` 与 `probe` 在端口不通时会自动 launch。
使用者只需一条命令，不必先手动开游戏，也不必记调试参数。

```
GameTrainer.exe sem solcesto read
  → 端口未就绪，尝试自动启动 Sol Cesto…
  → 自动启动成功，端口已就绪
  → 3.2 秒读出全部语义
```

适配包需要 `connection.installDir`（游戏安装目录）。未填时会按
适配包同级目录 → 常见盘符 `\Games\` 下按名匹配依次探测。

### 实测

```
GameTrainer.exe launch solcesto
  → exe: E:\Games\Sol Cesto\SolCesto.exe
  → 启动成功，CDP 端口 9222 已就绪
  → 1.8 秒返回，exit 0
```

### 两个必须记住的实现约束

1. **必须 `UseShellExecute = true`**。
   最初用 `false` + 继承 stdout，结果：命令行不返回（像卡死），
   而且杀掉父进程会连带杀掉游戏。游戏是长期运行的进程，生命周期不该绑在一次 CLI 调用上。
2. **`UseShellExecute=true` 下不能用 `ArgumentList`**，含空格的参数要自己加引号
   （见 `GameLauncher.QuoteArg`）。
3. **只等端口不够，必须等 page 目标**。
   `/json/version` 一响应就返回，但此时游戏页面可能还没加载，
   `/json/list` 里没有 `type=page`，后续操作会报"没有找到可调试的 page 目标"。
   正确做法是轮询到出现 url 非空的 page 目标为止。

### 已知边界

- **不自动杀进程**：已有同名进程时不替用户做决定，只提示并引导
- **启动器吞参数**：部分游戏的 launcher 会二次转发并丢掉 `--remote-debugging-port`，
  此时 `launch` 会报"进程仍在运行但端口未就绪"，需改用方案 C
- **仍依赖 CDP**：方案 A 只是藏起参数，没有脱依赖。真正脱依赖要做方案 C（AOB 锚点固化）

## 内存后端（memory_aob）

适配包现在支持原生进程。三种绑定全部可用：

| kind | 目标 | 状态 |
|---|---|---|
| `instance_var` | Construct3 实例变量 | ✅ 已测 |
| `indexeddb_key` | IndexedDB 键值 | ✅ 已测 |
| `memory_aob` | 原生进程内存 | ✅ 已测（自建测试靶） |

### 用法

```powershell
# 探测（两阶段收窄，中间候选落盘）
probe --native <进程名> <当前值> [double|int32|float|int64]
probe --native <进程名> <新值> <类型> --next

# 填入适配包后，与 Web 游戏用法完全一致
sem <gameId> read
sem <gameId> write gold 8888
```

### 实现要点

- `MemoryBackend`：附加 → 反作弊闸 → AOB 定位 → 偏移 → 编解码
- 复用 `PatchEngine.ResolveAddresses / ReadBytes / WriteBytes`，不重写扫描逻辑
- **每次操作重新扫描，不缓存地址**：进程重启后地址必变，缓存会写到随机地址
- 写入前必读前值压入回滚栈，读不到就拒绝盲写

### 实测数据

测试靶 `GtTestTarget`：Gold(double)@+0x00 … Anchor@+0x28

```
第1轮 按 100 扫描        → 76 个候选
改值  100 → 4321
第2轮 4321 --next 过滤   → 1 个候选
     地址 0x1F780111C50（与测试靶自报状态块地址一致）
     自动生成唯一签名，偏移 +0x10
读 4321 → 写 8888 → 读回 8888
```

独立验证：PowerShell 原生 `ReadProcessMemory` 确认地址内容为 7777.0
（字节 `00 00 00 00 00 61 BE 40`）。

### 两个踩过的坑

**1. 偏移符号写反**
`ToBinding` 一开始生成 `-0x10`，但签名起点是前文开头，
目标字段在起点**之后**，应为 `+0x10`。写反会读到前文里的垃圾值
（实测读出 1.06E-313）。

**2. 一次性给几十个"唯一"签名等于没给**
首扫 56 个候选时，其中 49 个都能生成"唯一"签名——使用者无法判断。
改为两阶段过滤：候选 >5 个时只提示收窄，不生成签名。

### 指针链（chain）

适配包现在支持 chain，与 aob 二选一：

```jsonc
{ "module": "GameAssembly.dll", "chain": ["0x1A2B3C", "0x10"], "type": "double" }
```

解析：模块基址 + chain[0] → 解引用 → + chain[1] → … → 终点。

**绝对地址模式**（module 留空）：chain[0] 是起始地址且**先解引用**，
后续才是偏移；只给一级视为直接地址。

比 AOB 稳定的场景：游戏更新后代码变了但数据结构没变。

实测（测试靶加了 VirtualAlloc 固定指针槽 0x30000000）：

```
chain: 0x30000000 → 解引用 → 0x2151F10CC60 → +0x00
读 100 → 写 6666 → 读回 6666
独立验证：ReadProcessMemory 确认状态块为 6666
```

**踩坑**：绝对地址模式第一版把 chain[0] 当起点消耗后，
只剩一级偏移被当作 last 只做加法、没解引用，读到指针本身（1.13E-311）。

### 未验证

- 真实 Unity / UE 游戏（只有自建测试靶）
- 反作弊命中时的只读切换（代码路径存在，未在真实反作弊环境测）

## GUI（场景 3）

### 四种交互样式（由适配包 style 字段决定）

| style | 控件 | 用于 |
|---|---|---|
| `fill` | 主按钮「回满」+ 折叠手动设置 | 血量、能量累积 |
| `step` | −/+ 步进 + 折叠手动设置 | 能量、攻击、魔法、能量累积上限、容量 |
| `number` | 输入框 + 应用按钮 | 金币、生命上限、累计金币 |
| `toggle` | 开关 + 「持久」勾选 | 防御（护盾） |

卡片网格按窗口宽度自适应列数（每列 ≥260px，最多 4 列），容器可纵向滚动。

### 持久监控器（PersistentWatcher）

勾选「持久」后每秒检查目标值，丢失就补回；退出对局自动停止。

判断「是否在局内」用 `engineOptions.inGameLayouts` 白名单
（Sol Cesto: `jeu_scene`, `boss`, `boss_combat`, `depot`）。
未配置时退化为「目标对象仍有实例」。

连续 10 次失败自动退出，不无限重试。切换适配包与退出程序时停止释放。

### UI 审查工具

`tools/review-ui.py` 从控件定义与布局参数核对 `ui-design.md` 的 16 条规范，
输出 `temp/validation/ui-review.md`。只做可验证的条款核对，不做审美判断。

**踩过的坑**：第一版正则 `Size\s*=\s*new Size` 会匹配到 `MinimumSize` 那行尾部，
导致报出错误的默认窗口尺寸结论。已改为 `(?<!Minimum)Size`。

### 真实操作验证（MCP + 截图，非仅源码）

```
点「启动游戏」→ 自动带端口启动 → 日志确认 → 端口就绪
累计金币输入框填 5000 → 点「应用」
  → [10:32:28] 累计金币: 999 → 5000  [需重载]
  → 独立查询 IndexedDB orEver = 5000
```

### 进入对局的正确操作（实测）

**先点骑士角色，再点右侧洞穴图标。** 地图上的星星/圆形节点是关卡选择，
带锁的节点表示未解锁，不能用来进游戏。

### 真实局内验证（MCP 操作 + 独立查询确认）

进入对局后 11 个值全部读取成功（耗时 36ms），三种交互实测通过：

| 交互 | 操作 | 结果 |
|---|---|---|
| 步进 | 点「攻击 +」 | pouvoir_force 2 → 3 |
| 回满 | 点「生命值 回满」 | vie 2 → 5/5 |
| 数值 | 填 5000 点「应用」 | orEver 999 → 5000 |

每项都用独立 CDP 查询核对游戏内真实值，不只看界面。

### 持久套盾完整验证（已测通）

```
[10:56:14] 防御: 0 → 1  [实时]
[10:56:14] [持久] 防御 监控已启动（每 1 秒检查，退出对局自动停止）
[10:56:37] [持久] 防御：持久补写：0 → 1
[10:56:51] [持久] 防御：持久补写：0 → 1
[10:58:25] [持久] 防御：检测到已退出对局，持久监控自动停止
```

两次人为清零均被自动补回；切场景到 `titreJeuNew` 后监控器正确自停。

### 窄窗口布局（1000x700 已验）

进入窄布局时自动收起日志，卡片获得双列/三列。
`_logPad` 已从局部变量提升为字段。

**踩坑**：第一版 `RelayoutForWidth` 无条件写回 58/42 列宽，
用户手动收起的日志会被窗口尺寸变化重新撑开。改为尊重 `_logCollapsed`。

### 状态语义（三态）

窄窗口截图曾暴露顶栏「读取失败」/卡片「不在此场景」/状态栏「已连接」互相矛盾。
根因是徽标只判断"有没有失败项"，没区分原因。改为：

| 情况 | 徽标 |
|---|---|
| 全部成功 | 绿「已连接」 |
| 部分成功（其余不在当前场景） | 绿「已连接」 |
| 全失败且是「无实例」 | 橙「不在局内」+ 提示进游戏后刷新 |
| 全失败且是连接错误 | 红「读取失败」 |

### 未验证

- 高 DPI（125%/150%）下的卡片布局
- 持久监控长时间运行（目前只测了约 2 分钟）

### 排查记录：一次误判

GUI 一度显示「不在此场景」但 `sem read` 能读到值。原因不是代码 bug——
当时游戏还在标题页(`titreJeuNew`)，`heros` 确实没有实例；
进入对局后刷新即全部成功。

期间用 CLI 传含双引号的 JSON 脚本反复失败，实际是 **PowerShell 吞掉了
内部双引号**（`{"obj":"heros"}` 变成 `{obj:heros}` → ReferenceError），
与 GUI 走 C# 字符串无关。排查 CLI 脚本时优先用文件传递，不要内联传参。

## 适配包体系（Game Adapter Pack）

### 设计意图

把「**调查方法**」和「**数值映射**」从结果里分离出来：

- `docs/GAME-ADAPTER-GUIDE.md` — 方法论：新游戏怎么判类型、怎么探测、怎么排错
- `adapters/<gameId>/adapter.json` — 每游戏一份映射，主程序不认游戏只认语义
- `Adapter/Semantics.cs` — 通用语义表（health/gold/energy/…）+ 别名匹配

新增游戏**不需要改主程序代码**，只需投一个 adapter.json。

### 命令

```
adapters                              列出已安装适配包
probe <port> [--save <id> <名> <exe>] 只读探测 + 生成骨架
sem <gameId> read [语义]              按语义读
sem <gameId> write <语义> <值>        按语义写（带区间校验）
```

### 探测器的 5 个阶段

1. CDP 通道可达性
2. 框架识别（construct3 / electron / nwjs / generic_js）
3. 运行时对象模型（对象类 + 实例变量）
4. 存储（IndexedDB 库与键数）
5. 语义匹配（按别名表打分）

**探测器只读，不改游戏状态。**

### 已知限制（务必记住）

**探测结果依赖当前场景。** 局内对象（主角、棋盘）只有进入对局才存在；
在标题页探测会得到「实例数 0」，语义绑定会全部落到 IndexedDB 上——而那通常是错的。
**必须先进入目标场景再探测。**

实测：Sol Cesto 在标题页探测出 12 个 IndexedDB 绑定，进对局后才发现真正的
`heros`.vie / .or / .capacite 才是该改的位置。

### 自动生成的绑定不可直接使用

`probe --save` 产出的绑定未经实测，`range` 为空，需要按
`docs/GAME-ADAPTER-GUIDE.md` 第五节的人工确认清单逐条核对。

## js_runtime 后端（NW.js / Electron / CEF）

### 已实现并通过真实游戏验证

目标游戏 Sol Cesto（Construct 3 / NW.js 0.86 / Chromium 123），启动参数 `--remote-debugging-port=9222`。

| 命令 | 作用 |
|---|---|
| `CdpClient` | HTTP `/json/list` 发现页面 + WebSocket `Runtime.evaluate` |
| `jsobj <port> [对象名]` | 列出有实例的对象类；给对象名则打印全部实例变量 |
| `jsdb <port> [库名]` | 列 IndexedDB 库；给库名则导出全部 object store 键值 |
| `js <port> "<js>"` | 任意 JS 求值 |
| `jsset <port> <db> <key> <val>` | 写 IndexedDB，打印写前值 |
| `jssetvar <port> <对象> <变量> <值>` | 写实例变量（官方 API） |
| `jsapply <profile> <patch> <值>` | profile 驱动，双模式 + 区间校验 + dry-run |

### 两条写入路径（不可混用）

| 类型 | 目标 | 生效方式 |
|---|---|---|
| storage 型（`storageKey`） | IndexedDB `c3-localstorage-<projectId>/keyvaluepairs` | 需 `location.reload()` |
| 实例变量型（`objectName`+`varName`） | Construct 3 运行时实例 | **实时生效**，界面立即刷新 |

### 关键 API 与陷阱

```javascript
// 访问运行时
const rt = self.c3_runtimeInterface._localRuntime;
const cls = [...rt.GetAllObjectClasses()].find(c => c.GetName() === 'heros');
const inst = [...cls.GetInstances()][0];
const names = inst._GetAllInstanceVariableNames();   // 变量名数组
const idx = names.indexOf('vie');
inst.GetInstanceVariableValue(idx);

// 参数顺序是 (索引, 值)，不是 (值, 索引)！
inst.SetInstanceVariableValue(idx, VAL);
```

**踩过的坑**：

1. `SetInstanceVariableValue(值, 索引)` 参数写反 → 静默失败或 `RangeError: invalid instance variable`。
   传 `(999, 10)` 时把 999 当索引越界；传 `(10, 8)` 时把 10 当索引、8 当值，读回仍是原值。
2. `GetInstanceVariableValue('vie')` 传名称返回 0，**必须传索引**。
3. `jsset` 打印写前值时，`NaN` 会被 JSON 序列化成 `null`，看起来像"原本就是 null"。
4. 局内对象（`heros` 等）只有进入对局才创建实例；主页阶段实例数为 0。
5. 二进制解析 leveldb WAL **不可靠**：`or` 键的字节模式会误命中 `mendiantLore` 字符串内部片段，
   导致读出 473/591 这类与实际无关的值。**优先用 CDP 读，不要手工解 leveldb。**

### Sol Cesto 已确认可改的数值

`heros` 实例变量共 35 个，索引如下：

| 索引 | 变量 | 含义 | 已验证 |
|---|---|---|---|
| 5 | `pouvoir_force` | 力量 | ✅ 2→20 |
| 6 | `pouvoir_magie` | 魔法 | — |
| 8 | `vie` | 当前血量 | ✅ 5→10 |
| 9 | `vieMax` | 血量上限 | ✅ 5→10 |
| 10 | `or` | 局内金币 | ✅ 0→999 |
| 11 | `or_multiplicateur` | 金币倍率 | — |
| 12 | `actions` | 行动数 | — |
| 13 | `capacite_seuilActuel` | 能量累积 | ✅ 0→4 |
| 14 | `capacite_seuilMax` | 能量累积上限 | — |
| 15 | `capacite` | 能量（界面数字） | ✅ 1→10 |
| 17 | `inventaireMax` | 背包容量 | — |
| 24 | `bouclierActif` | 护盾 | — |
| 33 | `soinMult` | 治疗倍率 | — |

局外：IndexedDB 的 `or`（✅ 6→999，重载页面生效）、`orEver`。

以上所有写入在重启游戏后**依然保持**，未出现存档损坏。

证据：`temp/validation/solcesto/ingame-vars.txt`

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
7. **js_runtime 后端仍是 CLI 形态**，GUI 尚未接入；构造对象的实例变量写入未做回滚记录。
8. **只验证了 Construct 3 一种引擎**，NW.js 原生变量、Electron、一般 JS 游戏未覆盖。
9. `tools/parse-solcesto-save.py` 是失败路线的遗留脚本，保留仅作参考，结论不可信（见上文第 5 条坑）。

## 维护

每到收尾：更新「最后校准」、快照表、真实跑过的命令与结果、缺陷表新增项、待办增删。删除失效状态，不写逐轮聊天记录。
线上 Release、CI、远端分支未核验时必须明确写「未核验」，不得写成已完成。
