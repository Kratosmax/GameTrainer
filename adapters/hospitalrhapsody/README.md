# Hospital Rhapsody 适配说明

## 环境

- Unity 2022.3.62 + IL2CPP v31
- `GameAssembly.dll` 42.4 MB / `global-metadata.dat` 10.7 MB
- 存档：`%LOCALAPPDATA%Low\<游戏名>\Save\Save1-3.json`
  —— **Base64 + AES 加密（熵 8.0），不可手改**，只能改运行时内存

## 内存结构（字段偏移来自 Il2CppDumper 的 dump.cs，全部实测验证）

```
PlayerData
  +0x10  UniqueID (string)
  +0x48  Userdata          ← 存档主体

Userdata
  +0x20  rolename (string)     ← 存档身份之一
  +0x28  logintime
  +0x2C  curlevel              等级
  +0x30  curexp                经验
  +0x40  curgold               总资产
  +0x44  starttime             开局时间 ← 存档身份之一（保存不变）
  +0x50  savetime              存档时间（保存时更新）
  +0x60..+0xB8                 一整排字典指针
  +0x88  myEmployees           Dictionary<int, Useremployee>

Useremployee（字典值对象）
  +0x10  employeeid
  +0x14  deptinx
  +0x18  curgood               好感度
  +0x1C  curbad                猥琐值
  +0x20  curgoodnum

Dictemployee（静态表，按 employeeid 反查）
  +0x10  id
  +0x14  employeetype
  +0x18  ename                 名字索引 → 名字表
  +0x24  salarygold
  +0x30  bmedical              医术
  +0x58  age                   年龄
```

`.NET Dictionary` Entry 布局：**步长 0x18**
```
+0x00 hashCode / +0x04 next / +0x08 key / +0x0C pad / +0x10 value(8字节指针)
```

## 关键坑（都踩过）

### 1. VirtualQueryEx 需要 PROCESS_QUERY_INFORMATION
漏了 `0x0400` 这一位，`OpenProcess` 会成功但 `VirtualQueryEx` 恒返回 0，
且 `GetLastError()` 也是 0 —— 极难排查，表现为"扫不到任何内存"。

### 2. 字典条目不能用 hashCode 过滤
内存里的字典有空洞与扩容残留，按 `hashCode == -1` 判断槽位有效会漏掉真实条目。
改用「指针自证」：读到的对象必须有有效 klass 且 ID 在合理范围。

### 3. 内存里有多份结构相同的 Userdata
靠"员工数最多"或"资金量级"都会选错——
实测曾出现 10 个员工读成 47 个、资金在 51~103 万之间跳变。
现在的判据：**所有字典指针有效 + starttime/savetime 是合理时间戳 + 锚点员工 ID 齐**。

### 4. 保存存档不能让定位缓存失效
`savetime` 每次保存都会变，但 `starttime` 和 `rolename` 不变。
缓存指纹用后两者，所以**保存存档不需要重新定位，只有读档/开新档才需要**。

### 5. 医术在静态表上，不在员工对象上
内存里没有从 Useremployee 指向 Dictemployee 的指针，只能按 employeeid 反查。
反查时必须校验 `employeetype(+0x14)` 在 1..20，否则会命中内存垃圾，
表现为姓名和数值全错（1001 会显示成别人的名字）。

### 6. 增量编译会骗人
`dotnet build` 显示"成功"但代码没重新编译，导致过多次误判。
改代码后建议加 `--no-incremental`。

## 用法

```powershell
$gt = "F:\git\temp\game-trainer\temp\build\GameTrainer\Release\net8.0-windows\GameTrainer.exe"

# 读（GUI 也支持）
& $gt sem hospitalrhapsody read                     # 全部
& $gt sem hospitalrhapsody read gold_total          # 总资产
& $gt sem hospitalrhapsody read employee_good       # 好感度
& $gt sem hospitalrhapsody read employee_bad        # 猥琐值
& $gt sem hospitalrhapsody read employee_medical    # 医术

# 写
& $gt sem hospitalrhapsody write gold_total 2000000
& $gt sem hospitalrhapsody write employee_good "2500=200"      # 指定员工
& $gt sem hospitalrhapsody write employee_good 200             # 全体员工
& $gt sem hospitalrhapsody write employee_medical 30
& $gt sem hospitalrhapsody write employee_bad "1001=0"

# 先看效果不写
& $gt sem hospitalrhapsody write employee_good "2500=200" --dry-run
```

## 未完成

**小游戏秒判定**（点击电脑进入的竖向滑动条）——**未实现**。

查过 `BeatGame` / `FishingGame` / `CatchMouse` / `ActionBase` / `ScrollSlider` /
`ServiceProgress` / `JerkOff` 全部失败：
- 字段范围匹配会命中上万条对象池模板
- `JerkOff` 的实际实例在内存里定位不到（协程类无实例、klass 反查不匹配）
- 曾误以为改 `0xA63CFE968+0x98`（fillSpeed）有效，实测是错觉

**可靠做法**：用 x64dbg 在 `JerkOff.Update`（RVA `0x448D80`）下断点，
读调用时的 `this` 指针。纯内存扫描这条路走不通。
