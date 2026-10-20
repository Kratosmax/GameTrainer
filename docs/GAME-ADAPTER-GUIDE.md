# 新游戏适配手册

**目标**：遇到一个没见过的单机游戏时，按本文档的流程摸清它的数据存放方式，产出一个可复用的适配包，而不是每次从零试错。

---

## 一、先判类型，再选路径

不同技术栈的破解路径完全不同。**先花 1 分钟判断类型，能省掉几小时的错误尝试。**

| 特征 | 类型 | 首选路径 |
|---|---|---|
| 目录有 `package.nw` / `nw.dll` / `nw_elf.dll` | NW.js（常配 Construct 3 / Cocos） | CDP |
| 目录有 `resources/app.asar` / `electron.exe` | Electron | CDP |
| 目录有 `UnityPlayer.dll` / `GameAssembly.dll` | Unity / IL2CPP | 内存（AOB / 指针链） |
| 目录有 `libglesv2.dll` + `d3dcompiler` 但无 nw/electron | CEF 套壳 | CDP 先试，失败转内存 |
| 单个 exe + 大量 .dll，无 Web 痕迹 | 原生程序 | 内存（AOB / 指针链） |

**判断命令**（只读，不启动游戏）：

```powershell
Get-ChildItem "<游戏目录>" -Filter *.dll | Select-Object Name
Get-ChildItem "<游戏目录>" -Filter package.nw
```

### 反证：这个判断什么时候会错

同一款游戏可能混用多套技术（如 Unity 内嵌 WebView 做 UI）。若 CDP 能连上但读不到业务数据，说明数据在原生层，应转内存路径。

---

## 二、CDP 路径（Web 技术栈）

### 2.1 建立通道

游戏必须以调试端口启动。**这是前提，没有端口就无法继续。**

```powershell
Start-Process "<游戏exe>" -ArgumentList '--remote-debugging-port=9222'

# 验证（1-2 秒内应返回版本 JSON）
Invoke-WebRequest 'http://127.0.0.1:9222/json/version' -UseBasicParsing
```

**坑**：部分游戏会忽略该参数（启动器二次转发、或已写死配置）。备选：

- 找游戏目录下的 `package.json`，看 `chromium-args` 字段
- 用启动器参数透传
- 实在不行转内存路径

### 2.2 一键探测

```powershell
GameTrainer.exe probe 9222
```

探测器按 5 个阶段跑，任一步失败会明确报出卡在哪：

1. **通道** — CDP 是否可达、页面目标
2. **框架** — 自动识别 construct3 / electron / nwjs / generic_js
3. **运行时** — 能否拿到对象模型
4. **存储** — IndexedDB 有哪些库、多少键
5. **语义** — 把发现的变量名匹配到通用语义

**探测是只读的**，不修改游戏任何状态。

### 2.3 关键前提：必须在「正确场景」下探测

**这是最容易踩的坑。** 游戏对象分两类：

- **常驻对象**（如进度、设置）：任何界面都存在
- **场景对象**（如主角、棋盘）：只有进入对局才创建

在标题页探测时，场景对象实例数为 0，探测结果只会给出 IndexedDB 绑定，**而那往往不是真正该改的位置**。

**正确做法**：先手工进到目标场景（进一局游戏），再跑探测。

验证方法：

```powershell
GameTrainer.exe jsobj 9222 <对象名>
# 输出 "共 N 个实例" 才说明对象存在
```

---

## 三、Construct 3 专项

### 3.1 运行时入口

```javascript
const rt = self.c3_runtimeInterface._localRuntime;
```

拿不到时依次尝试：`window.C3` → `C3.runtime` → 检查是否用了 worker 渲染（此时需连 worker 目标）。

### 3.2 枚举对象与变量

```javascript
const classes = [...rt.GetAllObjectClasses()];          // 全部对象类（常见上千个）
const cls = classes.find(c => c.GetName() === 'heros'); // 按名字找
const inst = [...cls.GetInstances()][0];                // 取实例

const names = inst._GetAllInstanceVariableNames();      // 变量名数组
const idx = names.indexOf('vie');                       // 必须转成索引
inst.GetInstanceVariableValue(idx);                     // 读
inst.SetInstanceVariableValue(idx, 123);                // 写（注意顺序！）
```

### 3.3 三个必踩的坑

| 坑 | 表现 | 正解 |
|---|---|---|
| `SetInstanceVariableValue(值, 索引)` 写成这个顺序 | 静默失败，或 `RangeError: invalid instance variable` | 参数是 **(索引, 值)** |
| 用变量名读 | 恒返回 0 | `GetInstanceVariableValue` 只接受索引 |
| 主页阶段找局内对象 | 实例数 0 | 先进入对局 |

### 3.4 存储位置

| 用途 | 位置 |
|---|---|
| 运行时 localStorage 插件 | IndexedDB `c3-localstorage-<projectId>` / objectStore `keyvaluepairs` |
| System 存档槽（多数游戏不用） | IndexedDB `c3-savegames-<projectId>` |

**判断用的是哪个**：

```powershell
GameTrainer.exe jsdb 9222
# 只出现 c3-localstorage-* → 游戏没使用 Save/Load 动作
```

### 3.5 实时 vs 需重载

| 写入目标 | 生效方式 |
|---|---|
| 实例变量 | **立即生效**，UI 同步刷新 |
| IndexedDB | 需 `location.reload()` 或重启游戏 |

---

## 四、通用语义映射

适配包的核心是把**实现名**映射到**语义**，上层命令与 UI 只认语义。

| 语义 | 含义 | 常见实现名 |
|---|---|---|
| `health` | 当前生命 | vie, hp, health, life, sante |
| `health_max` | 生命上限 | vieMax, maxHp, maxHealth |
| `gold` | 主要货币 | or, gold, coin, money, pieces |
| `gold_total` | 累计货币 | orEver, totalGold |
| `energy` | 能量/行动力 | capacite, energy, mana, stamina |
| `energy_charge` | 能量累积 | capacite_seuilActuel, charge |
| `energy_charge_max` | 累积上限 | capacite_seuilMax, chargeMax |
| `attack` | 攻击/力量 | pouvoir_force, attack, atk, strength |
| `magic` | 魔法 | pouvoir_magie, magic, spell, int |
| `defense` | 防御/护盾 | bouclierActif, defense, shield, armor |
| `level` | 等级 | level, niveau, lvl, exp |
| `capacity` | 容量 | inventaireMax, capacity, bag, slots |

新增语义时改 `src/GameTrainer/Adapter/Semantics.cs`，别名表越全，自动探测命中率越高。

### 反证

语义名是通用抽象，但**同一语义在不同游戏里的行为可能不同**。例如 `energy` 在某些游戏是「每回合行动点数」，在另一些是「魔法值上限」。
映射前应确认该数值在界面上的实际表现，不能只看名字像就绑定。

---

## 五、产出适配包

```powershell
# 自动生成骨架（未实测，需人工确认）
GameTrainer.exe probe 9222 --save <gameId> "<显示名>" "<exe名>"
```

生成位置：`adapters/<gameId>/adapter.json`

### 人工确认清单

自动生成的绑定**不可直接使用**，必须逐条核对：

- [ ] 绑定指向的是**运行时真实数据**，不是只读快照
- [ ] 在正确场景下读到合理的值（对照游戏界面）
- [ ] 写入后界面立即变化（实例变量）或重载后变化（IndexedDB）
- [ ] 填了 `range`，防止写出荒谬值
- [ ] `mode` 选对：有联机可能用 `offline_only`
- [ ] 在 `notes.pitfalls` 里记下这个游戏特有的坑

### 使用适配包

```powershell
GameTrainer.exe adapters                     # 列出已安装
GameTrainer.exe sem <gameId> read            # 读全部语义
GameTrainer.exe sem <gameId> read gold       # 读单个
GameTrainer.exe sem <gameId> write health 8  # 写（带区间校验）
GameTrainer.exe sem <gameId> write health 8 --dry-run
```

---

## 六、内存路径（原生程序）

CDP 不可用时走这条。工具侧能力：

```powershell
GameTrainer.exe scan   <profileId> <值> [type]   # 首扫
GameTrainer.exe narrow <profileId> exact <新值>  # 再扫
GameTrainer.exe aob    <profileId> "<签名>"      # 验证签名唯一性
GameTrainer.exe apply  <profileId> <patchId> --dry-run
```

### 关键原则

- **AOB 签名不能包含会被自己改掉的字节**，否则写一次签名就永久失效
- **签名必须唯一命中**，不唯一就加长或加 `??` 通配
- 数值类型（int32 / double / float）必须与游戏内实际一致，猜错会写出垃圾

### 反证

**JS 引擎类游戏不要走内存路径。** V8 的对象会被 GC 移动，地址每轮都变，
而且直接改内存会破坏 tagged pointer，引擎不认。实测坐实过两次失败。

---

## 七、通用排错

| 现象 | 原因 | 处理 |
|---|---|---|
| CDP 连不上 | 没带调试端口 / 启动器吞了参数 | 检查启动参数；看 package.json 的 chromium-args |
| 引擎识别为 generic_js | 不是标准 Web 游戏 | 走内存路径 |
| 对象实例数 0 | 不在对应场景 | 先进游戏到目标界面 |
| 写入 after 不等于目标值 | 参数顺序错 / 有校验拦截 | 对照本文档 3.3 检查 |
| 写入生效但重启后还原 | 改的是内存，游戏从存档重新加载 | 改存档侧（IndexedDB）而非实例变量 |
| 读到的值与界面不符 | 绑错了变量（有多个相似字段） | 用 `jsobj` 打印全部变量逐个对照 |

---

## 八、给后续会话的提示

产出适配包时，**务必填 `notes` 三段**：

- `steps`：怎么摸到的（可复现的路径）
- `pitfalls`：踩过什么坑（避免重复）
- `keyFindings`：关键入口（运行时路径、存储位置）

这三段是给下一个人（或下一个 AI 会话）的，比结果本身更重要。
