# GameTrainer 1.0.0

首个正式版本。单机游戏内存/存档修改器，一个程序 + 每游戏一个适配包。

## 主要能力

- **适配包体系**：`adapters/<gameId>/adapter.json` 描述一个游戏怎么改，换游戏只换适配包
- **三种绑定后端**：`instance_var`（Construct3/NW.js 运行时变量）、`indexeddb_key`（存档）、`memory_aob`（内存签名 + 指针链）
- **语义层**：12 个通用语义（生命/金币/等级…）+ 别名表，`sem <游戏> read/write <语义>` 直接取值改值
- **安全闸门**：写内存前四重校验（签名唯一 / 前值匹配 / 区间 / 字节上限），反作弊存在时强制只读
- **内置适配**：Sol Cesto（Construct3 + NW.js 0.86）

## 两个分发通道

| 通道 | 体积 | 要求 |
|---|---|---|
| **Lite** | 约 200 KB | 需已安装 .NET 8 Desktop Runtime |
| **Full** | 约 61 MB | 自带运行时，开箱即用 |

程序通过同目录 `channel.txt` 识别自己属于哪个通道，只接受匹配通道的更新。

## 更新机制

内置签名更新器。`GameTrainer.exe update check` 检查，`update install` 安装。
清单由 RSA-3072 私钥签名，程序内置公钥验签——**即使 GitHub 账号被攻破，攻击者也无法伪造更新**。

## 已知缺口

- `memory_aob` 只用自建测试靶验证，未在商业游戏上跑过
- 真实 Unity / UE 游戏未验证
- 深色主题、125%/150% DPI 未验证
- Tab 遍历顺序未经人工确认

## 校验

下载后请核对 `SHA256SUMS.txt`。全部产物均由 CI 构建并附签名清单。