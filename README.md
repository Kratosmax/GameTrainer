# GameTrainer

单机游戏内存 / 存档修改器。一个托盘程序 + 每个游戏一份 profile（`profiles/*.json`），换游戏只换 profile，程序不动。

**只处理单机内容。** 不做反作弊绕过、不做联机内容、不做 DRM 破解。

- 当前版本：`0.1.0`（版本真源：`Directory.Build.props` 的 `GameTrainerVersion`）
- 平台：Windows 10 / 11 x64
- 框架：.NET 8 + WinForms

---

## 一、使用者教程

### 系统要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10 x64 (1809+) / Windows 11 |
| 运行时 | Lite 包需预装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（x64）；Full 包已自带 |
| 权限 | 普通用户即可。仅当目标游戏本身以管理员运行时才需要同等级提权 |
| 显示 | 96 DPI 基线；已适配 125% / 150% 缩放 |

### 下载选择

| 包 | 适用 | 特点 |
|---|---|---|
| `GameTrainer-<ver>-Lite.zip` | 已装 .NET 8 运行时 | 体积小，解压即用 |
| `GameTrainer-<ver>-Full.zip` | 未装运行时 | 自带运行时，体积大 |

包内 `channel.txt` 标记通道，`version.txt` 标记版本。自动更新只接受同通道包。

### 安装与运行

便携式，无需安装：

1. 解压到任意目录（不要放在受系统保护的位置）。
2. 双击 `GameTrainer.exe` 打开主窗口，或运行 `GameTrainer.exe --background` 只驻留托盘。
3. 首次启动会在程序目录下创建 `snapshots/` 与 `audit.log`。

### 主要工作流

```
选择游戏(左侧栏) → 附加到游戏 → 逐个打开修改项开关 → 用完全部回滚
```

命令行等价操作：

```powershell
$gt = "<解压目录>\GameTrainer.exe"

& $gt profiles                              # 列出全部 profile
& $gt check    <profileId>                  # 附加 + 反作弊检查（只读）
& $gt aob      <profileId> "<AOB>"          # 测签名唯一性（只读）
& $gt scan     <profileId> <value> [type]   # 首扫
& $gt narrow   <profileId> exact <v>        # 再扫：精确 / changed / unchanged / range
& $gt apply    <profileId> <patchId> --dry-run   # 只校验不写
& $gt apply    <profileId> <patchId>             # 真写
& $gt revert   <profileId> <patchId>             # 回滚
& $gt revert   <profileId> <patchId> --by-profile # 跨进程补救还原
& $gt snapshot <profileId> "说明"           # 存档快照
& $gt restore  <profileId> <snapshotId>     # 从快照恢复
& $gt save-edit <profileId> <patchId>       # 存档编辑
& $gt gui                                   # 托盘统一入口
```

### 用户数据位置

全部在程序目录内，不写系统目录：

| 内容 | 位置 |
|---|---|
| profile | `profiles/*.json` |
| 存档快照 / 扫描状态 / 激活记录 | `snapshots/` |
| 写入与回滚审计 | `audit.log` |
| 开机自启注册项 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\GameTrainer` |

### 更新

自动更新通道尚未接入（见「AI 继续开发」的待办）。当前版本请手动替换 EXE 与 DLL，保留 `profiles/` 和 `snapshots/`。

### 卸载

1. 设置页关闭「开机自动启动」（会删除 Run 注册项）。
2. 托盘菜单选「全部回滚并退出」，确认没有残留修改。
3. 直接删除程序目录。

### 常见故障

| 现象 | 原因与处理 |
|---|---|
| `OpenProcess 失败 (Win32 5)` | 目标以管理员运行 → 本工具也要提权；或目标已退出 |
| `AOB 未命中` | 签名里可能含被自己改掉的字节；或游戏已更新需重扫 |
| `AOB 不唯一` | 签名太短，加长或用 `module` 限定，或改用指针链 |
| `前值校验失败` | 游戏内数值已变，这是保护生效，不是错误 |
| 反作弊闸阻断 | 设计行为，硬阻断且不可关闭 |
| 关闭窗口后找不到程序 | 已最小化到托盘，双击托盘图标恢复 |

---

## 二、自行编译教程

### 工具链

| 项 | 版本 |
|---|---|
| .NET SDK | 8.0.x（实测 8.0.426） |
| PowerShell | 5.1 或更高 |
| 打包依赖 | 无（用内置 `Compress-Archive`） |

### 构建与测试

```powershell
# 编译（0 警告 0 错误为目标）
dotnet build src/GameTrainer/GameTrainer.csproj -c Release

# UI 截图验证：真实窗口渲染 + 材质返回值落盘
dotnet build src/UiShot/UiShot.csproj -c Release
temp/build/UiShot/Release/net8.0-windows/GtUiShot.exe . temp/ui-shot

# 对比度核算（WCAG 2.1，29 项门槛检查）
python tools/check-contrast.py
```

产物目录：`temp/build/<项目名>/Release/net8.0-windows/`。所有构建中间物与产物都落在 `temp/`，不进源码树。

### 打包

```powershell
# 本地预览（产物在 temp/preview，带 -preview 与 PREVIEW-NOTICE.txt）
powershell -File build/package.ps1 -PreviewOnly

# 只打 Lite / 只打 Full
powershell -File build/package.ps1 -Channel Lite -PreviewOnly

# 正式产物（产物在 artifacts/）
powershell -File build/package.ps1
```

脚本从 `Directory.Build.props` 读版本，不要在别处再写一份。

### 平台限制

- 仅 Windows x64。`ApplicationManifest` 为 `asInvoker`，不默认要求管理员。
- DWM 材质需 Windows 11 build 22621+；更低版本自动回退为不透明表面，功能不受影响。
  当前材质只作用于标题栏（系统圆角 + Acrylic）：WinForms 用 GDI 绘制不透明客户区，
  若把 DWM frame 扩展到整个客户区，DWM 会在绘制结果之上再叠一层材质，导致整窗发白、
  正文不可读。这是框架能力边界，见 `CODEX_PROGRESS.md` 的「关于全窗毛玻璃的能力边界」。

---

## 三、AI 继续开发引导

**第一入口：[`CODEX_PROGRESS.md`](CODEX_PROGRESS.md)**。接续工作前先完整读它，再按其中的校准命令核对现状。

### 项目结构

```
game-trainer/
├── Directory.Build.props       版本真源 + 构建输出重定向到 temp/
├── GameTrainer.sln
├── README.md
├── CODEX_PROGRESS.md           AI 接续索引
├── docs/
│   └── GAME-ADAPTER-GUIDE.md   新游戏适配手册（方法论）
├── adapters/                   适配包：一个游戏一份
│   └── solcesto/adapter.json
├── build/package.ps1           四包构建（当前实现 Full/Lite ZIP）
├── tools/check-contrast.py     WCAG 对比度核算
├── profiles/                   旧格式 profile（内存后端用）
├── src/
│   ├── GameTrainer/            主程序
│   │   ├── Program.cs          命令行入口与命令分发
│   │   ├── GuiForm.cs          主窗口
│   │   ├── SettingsForm.cs     统一设置页
│   │   ├── AppVersion.cs       从程序集读版本（真源在 Directory.Build.props）
│   │   ├── Adapter/            适配包体系
│   │   │   ├── Semantics.cs      通用语义表与别名匹配
│   │   │   ├── GameAdapter.cs    适配包模型与校验
│   │   │   ├── ProbeEngine.cs    只读探测器（5 阶段）
│   │   │   ├── AdapterRunner.cs  按语义读写执行器
│   │   │   └── GameLauncher.cs   带调试参数启动游戏（方案 A）
│   │   ├── Js/CdpClient.cs     CDP 客户端（NW.js/Electron/CEF）
│   │   ├── Core/               热键、单实例、自启、审计、反作弊、激活状态
│   │   ├── Engine/             PatchEngine / SaveEngine / Scanner
│   │   ├── Memory/             进程内存、AOB、数值编解码
│   │   ├── Native/             Win32 P/Invoke + WindowMaterial(DWM)
│   │   ├── Profiles/           profile 模型与校验
│   │   └── UI/                 设计令牌与全部自绘控件
│   ├── TestTarget/             自测靶进程
│   ├── Probe/                  内存读取验证工具
│   └── UiShot/                 UI 截图验证工具
└── temp/                       构建、截图、验证、预览产物（不入库）
```

### 关键入口与约束

- **版本来源**：`Directory.Build.props` → `GameTrainerVersion` → 程序集元数据 → `AppVersion.Display`。改版本只改一处。
- **设计令牌**：`src/GameTrainer/UI/Tokens.cs` 是唯一颜色/间距/字体来源。业务代码不允许出现裸颜色常量。
- **安全边界（不可放宽）**：写入前四道校验（签名唯一 / 前值 / 区间 / 字节上限）；检测到反作弊即强制只读；不做任何绕过。
- **回滚边界**：正常退出与目标进程退出会自动回滚；修改器被强杀不会。靠 `snapshots/active-state.json` + `revert --by-profile` 补救。
- **AOB 铁律**：签名中绝不能包含会被本次写入改掉的字节。

### 最低验证命令

```powershell
dotnet build GameTrainer.sln -c Release -m:1          # 期望 0 警告 0 错误
temp/build/UiShot/Release/net8.0-windows/GtUiShot.exe . temp/ui-shot
python tools/check-contrast.py                         # 期望 exit 0
powershell -File build/package.ps1 -Channel Lite -PreviewOnly
```

UI 改动必须看真实渲染截图，不能只凭源码判断；`temp/ui-shot/*.png` 要逐张查看。

### 发布授权边界

构建 ≠ 发布。提交、推送、打标签、创建 Release、覆盖线上资产都是独立动作，需用户单独授权。

### 禁止提交

`temp/`、`artifacts/`、`bin/`、`obj/`、任何私钥/令牌/用户存档/日志正文。

### 已知待办

- 自动更新模块（清单签名、下载暂存、包预检、外部替换器）尚未实现。
- 安装器（Inno Setup）尚未接入，目前只分发 ZIP。
- GitHub Actions 工作流尚未建立。
