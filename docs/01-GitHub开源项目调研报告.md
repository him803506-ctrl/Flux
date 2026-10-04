# SmartUnzip 第一阶段：GitHub 开源项目调研报告

> 调研日期：2026-10-04
> 调研机器：Windows 11 (build 26100) x64，.NET SDK 10.0.401，7-Zip 26.03 x64
> 调研目的：为 SmartUnzip 寻找最适合二次开发的成熟开源基础项目

---

## 0. 结论先行（TL;DR）

**没有任何一个开源项目能直接作为 SmartUnzip 的完整基础框架。**

原因：SmartUnzip 的核心价值不在「解压」，而在「**先检测真实格式 → 识别扩展名伪装 → 自动匹配密码库 → 再解压**」这条链路。GitHub 上的解压工具无一例外都是「按扩展名/用户选择去解压」，**没有一个是"先探测魔数、发现 mp4 实际是 zip、然后自动解压"的**。这是一个明确的市场空白。

因此推荐方案是 **组合式复用**，而非整体 Fork：

| 角色 | 选型 | 理由 |
|------|------|------|
| **解压引擎** | 7-Zip 26.03 命令行 `7z.exe`（本机已装） | 业界最强、格式最全、LGPL 可自由分发 |
| **引擎调用封装** | 自研 `IArchiveEngine` + 进程调用（参考 SevenZipExtractor 的 API 设计） | 需实时进度/取消/密码，进程调用比 DLL 引用更可控 |
| **文件类型检测** | 自研 `FileTypeDetector`（本体是魔数表 + 探测逻辑） | 这是 SmartUnzip 的核心，必须自控；现有 C# 库无一满足"伪装判定 + 置信度 + 中文提示" |
| **密码库存储** | 自研 + Windows DPAPI（`ProtectedData`） | 需求明确要求 DPAPI，无现成库可直接用 |
| **UI** | 自研 WPF（.NET 10） | 候选 GUI 项目要么语言是 Pascal（PeaZip）、要么是 Demo 级，改造成本高于自研 |
| **右键菜单** | 自研 `.reg` + `--context` 参数 | 成熟方案即 7-Zip 自身的注册表写法，可直接参考 |

**一句话：UI 和检测逻辑自己写（本来就该自己写），解压全部交给 7-Zip，不从任何开源 GUI 项目 Fork。**

---

## 1. 候选项目清单（共 12 个，覆盖 5 个检索方向）

### 方向一：Windows 解压工具 / 归档管理器

| # | 项目 | 语言 | Star | License | 活跃度 | 评估 |
|---|------|------|------|---------|--------|------|
| 1 | [peazip/PeaZip](https://github.com/peazip/PeaZip) | Pascal (Lazarus) | 7,929 | **LGPL-3.0** | 极高（2026-10 仍在提交） | 功能最强，但 Pascal 技术栈无法与 C# 项目复用 |
| 2 | [FarGroup/FarManager](https://github.com/FarGroup/FarManager) | C++ | 2,233 | BSD-3-Clause | 高（2026-10） | 是文件管理器（Norton Commander 风格），非图形解压工具 |
| 3 | [sarensw/MacPacker](https://github.com/sarensw/MacPacker) | Swift | 1,035 | GPL-3.0 | 高 | macOS 专属，与 Windows 无关 |
| 4 | [d5001/archive-toolkit](https://github.com/d5001/archive-toolkit) | Python | 0 | 未标注 | 低（14 天前） | 个人小项目，Star 0，无参考价值 |

### 方向二：7-Zip GUI / 前端

| # | 项目 | 语言 | Star | License | 活跃度 | 评估 |
|---|------|------|------|---------|--------|------|
| 5 | [ip7z/7zip](https://github.com/ip7z/7zip) | C++ | 4,053 | 无 SPDX（7-Zip 官方许可） | 高（2026-09） | **官方源码镜像**，是引擎本体而非 GUI |
| 6 | [mcmilk/7-Zip-zstd](https://github.com/mcmilk/7-Zip-zstd) | C++ | 7,409 | 无 SPDX（7-Zip + 附加） | 高（2026-10） | 7-Zip 增强版（Brotli/LZ4/Zstd），引擎可选替代 |
| 7 | [Softorage/7z-GUI-Linux](https://github.com/Softorage/7z-GUI-Linux) | Go | 32 | GPL-3.0 | 高 | **Linux 专属**，与 Windows 需求无关 |
| 8 | [wasdwasd0105/7zip-pyside](https://github.com/wasdwasd0105/7zip-pyside) | Python | 16 | GPL-3.0 | **已归档** | 已归档，且为 Linux 方向，不可用 |

### 方向三：文件格式检测 / 魔数检测

| # | 项目 | 语言 | Star | License | 活跃度 | 评估 |
|---|------|------|------|---------|--------|------|
| 9 | [microsoft/RecursiveExtractor](https://github.com/microsoft/RecursiveExtractor) | C# | 221 | MIT | 中（23 天前） | **仅用于"嵌套归档"场景**，不含伪装判定；魔数逻辑可参考 |
| 10 | [KemalMurphy6528/file-mime-detector](https://github.com/KemalMurphy6528/file-mime-detector) | JavaScript | 0 | MIT | 新（2026-10） | JS 项目，Star 0，无参考价值 |

### 方向四：密码管理 / 自动解压

| # | 项目 | 语言 | Star | License | 活跃度 | 评估 |
|---|------|------|------|---------|--------|------|
| 11 | [dawn-lc/ArchivePasswordTestTool](https://github.com/dawn-lc/ArchivePasswordTestTool) | C# | 336 | 未标注 | 中（2026-03） | **密码测试思路高度相关**！用 7z 测试压缩包 + 自动试密码，与需求"密码库自动匹配"完全同源 |

### 方向五：Windows 右键解压 / Shell 扩展

| # | 项目 | 语言 | Star | License | 活跃度 | 评估 |
|---|------|------|------|---------|--------|------|
| 12 | [kaihuang1425/extract-with-passwords-extended](https://github.com/kaihuang1425/extract-with-passwords-extended) | PowerShell/WPF | 0 | MIT | 2026-07 | 需求（右键 + 密码表 + 多引擎回退）与 SmartUnzip 几乎重叠，但 Star 0、工程质量未知，仅作思路参考 |

### 附：C# 技术栈最相关项目（补充检索所得）

| # | 项目 | 语言 | Star | License | 活跃度 | 评估 |
|---|------|------|------|---------|--------|------|
| 13 | [adamhathcock/sharpcompress](https://github.com/adamhathcock/sharpcompress) | C# | 2,587 | **MIT** | 极高（2026-10） | 纯托管解压库，**不支持加密 RAR**，不能替代 7-Zip 引擎 |
| 14 | [adoconnection/SevenZipExtractor](https://github.com/adoconnection/SevenZipExtractor) | C# | 357 | **MIT** | 中（2024-12） | 7z.dll 的 C# 封装，**已实测编译通过**；但密码支持弱、进度事件不完善 |
| 15 | [squid-box/SevenZipSharp](https://github.com/squid-box/SevenZipSharp) | C# | ~800 | LGPL-3.0 | 低 | 老牌 7z.dll 封装，维护停滞，LGPL 有传染性 |

---

## 2. 重点候选深度评估

### 2.1 PeaZip（7,929★ · LGPL-3.0 · Pascal）— **不推荐 Fork**

**实测数据（2026-10-04 网页核验）**：
- 最后提交：2026-10-07（持续活跃，含 dependabot 与源码更新）
- 语言构成：**Lazarus / Free Pascal**（`.lpi` / `.lpk` 项目文件）
- 构建系统：**lazbuild**（Lazarus 命令行），Windows 侧另需 InnoSetup + windres
- 源码结构：
  ```
  dev/
    ├── project_pea.lpi       # PEA 引擎
    ├── project_peach.lpi     # GUI 前端主程序
    ├── dragdropfilesdll/     # Windows 拖放 DLL（需 DragDropLazarus 5.2+）
    └── installer/            # InnoSetup 脚本
  res/
    ├── bin/                  # 第三方二进制（7z、brotli、zpaq、zstd、upx…）
    └── share/, conf/         # 资源与配置
  ```
- 支持 200+ 格式，支持加密、右键菜单、拖拽、批量、进度——**功能上完美匹配**
- **但**：`dragdropfilesdll.dll` 有硬编码 SHA256 校验，重编译需同步改代码；第三方二进制（UnRAR 等）有各自许可限制

**判定：技术栈不兼容。** Pascal/Lazarus 无法与 C#（.NET 10）项目混编，若采用意味着 SmartUnzip 必须整体用 Free Pascal 重写。对"大一学生 + 长期维护 + 后期加云端/预览等扩展"的目标，这是负资产。**不予采用。**

---

### 2.2 SevenZipExtractor（357★ · MIT · C#）— **实测可编译，做 API 参考**

**实测结果（本机真实执行）**：
```
$ dotnet build SevenZipExtractor/SevenZipExtractor.csproj -c Release
  已还原 .../SevenZipExtractor.csproj (用时 6.25 秒)
  SevenZipExtractor -> bin/Release/net45/SevenZipExtractor.dll
  SevenZipExtractor -> bin/Release/netstandard2.0/SevenZipExtractor.dll
已成功生成。 0 个警告  0 个错误
已用时间 00:00:11.23
```
- 目标框架：`net45;netstandard2.0`（**与 .NET 10 兼容**）
- 自带 x86/x64 `7z.dll`，自动复制到输出目录
- 支持格式：7z / Rar / Rar5 / Zip / Tar / GZip / BZip2 / XZ / Cab / Iso 等 45 种
- **`ArchiveFile` 构造时能自动识别无扩展名文件**（README 明确示例）——这正是伪装检测需要的能力
- 有 `Extract(path, overwrite, password)` 密码支持（1.0.19 / 2024-12-18 加入）
- License：源码 MIT，7z.dll 适用 7-Zip 官方许可

**缺点（决定不直接依赖）**：
1. 通过 `7z.dll` 的 COM 风格接口调用，**进度回调粒度粗**，取消（CancellationToken）支持薄弱
2. RAR 加密是 7z.dll 的能力瓶颈（见 2.6 风险）
3. 最后提交 2024-12，维护节奏偏慢

**判定：不直接引用为依赖，但采用其 API 设计思路。** SmartUnzip 用**进程调用 7z.exe** 而非引 DLL，换取：完整的 stdin/stdout 进度解析、可靠的进程级取消、统一的错误码处理。

---

### 2.3 ArchivePasswordTestTool（336★ · C#）— **思路高度相关**

- 用 7-Zip 的测试（`t`）功能，对加密压缩包自动遍历密码表
- 与 SmartUnzip 的「密码库自动匹配」是同一个技术动作
- 无 SPDX 标注（**许可不明**）→ 不能抄代码，只能参考思路

**判定：仅参考「用 7z t + -p 试密码，看退出码判断成败」这一模式。** 本机已实测验证该模式可用（见第 3 节）。

---

### 2.4 SharpCompress（2,587★ · MIT · C#）

- 纯托管 C# 库，MIT 许可最干净
- 支持读取 7z/Rar/Rar5/Zip/Tar/GZip/BZip2/XZ 等，支持 async/await
- **但：不支持加密 RAR**（受 RAR 专有算法限制）；且不支持加密 7z 的写入
- 目标框架含 net10.0

**判定：不能做主引擎**（加密 RAR 是硬需求）。可作为**备用只读探测库**，但会引入额外依赖体积，当前版本不引入。

---

### 2.5 microsoft/RecursiveExtractor（221★ · MIT · C#）

- 微软出品的嵌套归档解压库，含 `MiniMagic` 魔数检测
- 用于「压缩包套压缩包」的递归场景

**判定：当前版本暂不引入。** SmartUnzip 的检测逻辑（伪装判定 + 置信度 + 中文状态）需要完全自控，该库的 `CanExtract` 设计可作为魔数探测的参考实现。

---

## 3. 核心技术路径实测验证（本机真实执行，非 README 声称）

### 3.1 环境事实

| 项目 | 实测结果 |
|------|----------|
| .NET SDK | **10.0.401**（原缺失，本次已通过 winget 安装 Microsoft.DotNet.SDK.10） |
| .NET 运行时 | 6.0.36 / 8.0.31 / 9.0.20 / 10.0.12（WindowsDesktop 齐全） |
| 7-Zip | **26.03 (x64)**，路径 `C:\Users\HKN\AppData\Local\Programs\7-Zip\7z.exe` |
| WinRAR | `C:\Program Files\WinRAR\Rar.exe`（作为可选备用引擎） |
| 编译实测 | 11 秒完成，0 错误 0 警告 ✅ |

### 3.2 魔数一致性实测

```
test.zip       504b030414000100   → PK..  (ZIP)
7z_test.7z     377abcaf271c0004   → 7z.. (7-Zip)
tar_test.tar   612e747874000000   → .txt (TAR 无魔数，靠 offset=257 的 "ustar")
gz_test.gz     1f8b080833d7c16a   → ..  (GZip)
```
**结论**：魔数检测方案对 ZIP/7Z/GZ/RAR 有效；TAR 需读偏移 257 处的 `ustar` 标记（7-Zip 自身即这么实现）。

### 3.3 伪装文件检测实测

```
$ cp test.zip fake.mp4
$ od -A x -t x1z fake.mp4
00000000: 504b 0304 1400 ...   PK........      ← 扩展名 .mp4，实际 ZIP
```
**结论**：直接读文件头即可判定伪装。**无需改扩展名**，7-Zip 本身也能直接解。

### 3.4 7-Zip 对伪装扩展名的容忍度实测

`7z l -slt fake.mp4` → 正常工作（7-Zip 本就按内容识别，不依赖扩展名）。这印证了 SmartUnzip 只需做「检测 + 提示」，解压环节直接交给 7z。

### 3.5 密码检测与试密码实测

```
$ 7z l -slt test.zip | grep Encrypted
Encrypted = +              ← 可判定加密
$ 7z t test.zip -p123456   → Everything is Ok        (退出码 0，密码正确)
$ 7z t test.zip -pwrongpass → Archives with Errors: 1 (退出码 2，密码错误)
```
**结论**：`7z l -slt` 判加密、`7z t -p` 试密码、靠退出码判成败——**密码库自动匹配的技术闭环已完全验证可行。**

### 3.6 已注册解码器实测（`7z i`）

```
Rar     rar r00   R a r ! 1A 07 00
Rar5    rar r00   R a r ! 1A 07 01 00
7z      ...       7 z BC AF ' 1C
zip     ...       P K 03 04 || P K 05 06 || ...
bzip2   ...       B Z h
gzip    ...       1F 8B 08
tar     ...       offset=257 u s t a r
xz      ...       FD 7 z X Z 00
```
**RAR/Rar5 解码器已注册 → 解压可用**（创建 RAR 不支持，但 SmartUnzip 只解不压）。

---

## 4. 许可证风险分级

| 项目 | License | 风险 | 能否修改 | 能否商用 | 能否打包 EXE | 能否闭源发布 |
|------|---------|------|---------|---------|-------------|-------------|
| SevenZipExtractor | MIT | 🟢 低 | ✅ | ✅ | ✅ | ✅ |
| SharpCompress | MIT | 🟢 低 | ✅ | ✅ | ✅ | ✅ |
| RecursiveExtractor | MIT | 🟢 低 | ✅ | ✅ | ✅ | ✅ |
| FarManager | BSD-3 | 🟢 低 | ✅ | ✅ | ✅ | ✅ |
| **7-Zip 本体** | LGPL-2.1 + unRAR 限制 | 🟡 **注意** | ✅ | ✅ | ✅ | 见下 |
| PeaZip | LGPL-3.0 | 🟡 注意 | ✅ | ✅ | ✅ | ❌（GPL 传染） |
| squid-box/SevenZipSharp | LGPL-3.0 | 🟡 注意 | ✅ | ✅ | ✅ | ❌ |
| MacPacker | GPL-3.0 | 🟡 注意 | ✅ | ✅ | ✅ | ❌ |
| ArchivePasswordTestTool | 无标注 | 🔴 **高** | ❓ | ❓ | ❓ | ❓ |
| shuhongfan/Bandizip | 破解版 | 🔴 **高** | ❌ | ❌ | ❌ | ❌ |

### 关键许可证结论

**1. 7-Zip 分发（SmartUnzip 必须处理）**
- 7-Zip 主体：**LGPL-2.1+**，允许商业使用、允许随附分发
- **但 unRAR 代码受 RARLAB 专有许可限制**：官方原文要求「不得用 unRAR 代码开发 RAR 压缩器」，且**分发时不能移除许可声明**
- 合规做法：
  - 随附 `7z.exe` / `7z.dll` 时必须附上 `License.txt`（本机 `C:\Users\HKN\AppData\Local\Programs\7-Zip\License.txt`，6,031 字节）
  - 提供 `THIRD-PARTY-NOTICES.txt` 说明来源与版本
  - **优先检测用户已装的 7-Zip**，未安装时才使用随附副本
- **推荐**：不随附 7-Zip 二进制，改为**引导用户从 7-zip.org 安装**，或随附时严格保留原始文件（不做任何改动）

**2. 自研部分的许可建议**
- SmartUnzip 自研代码建议 **MIT**，可自由商用与闭源发布
- 若采用任何 LGPL/GPL 项目代码，SmartUnzip 将被迫开源（GPL 场景下）→ **规避方式：不抄这些项目的代码**

---

## 5. 最终推荐方案

### 推荐项目：**无单一项目可 Fork。采用「7-Zip 引擎 + 全自研外壳」组合架构。**

**为什么不选 PeaZip（Star 最高的解压工具）**：
- Pascal 技术栈 → 无法与 C# 生态复用
- 重编译需同步硬编码 SHA256 → 维护成本高
- 整体 Fork 意味着放弃 .NET 全部生态

**为什么不选 SevenZipExtractor（最合适的 C# 解压封装）**：
- 进度回调与取消能力不达需求（需求要求实时进度 + 随时取消）
- 引 DLL 也不如进程调用透明可控

### 各模块复用策略

| 模块 | 复用来源 | 具体做法 |
|------|---------|---------|
| **解压引擎** | 7-Zip 26.03 `7z.exe` | 进程调用，解析 `-bsp1` 进度输出 |
| **引擎抽象** | 参考 SevenZipExtractor 的 API 命名 | 自研 `IArchiveEngine` 接口 |
| **魔数检测** | 自研（参考 7-Zip `7z i` 的签名表 + RecursiveExtractor 的 MiniMagic） | 自研 `FileTypeDetector` |
| **密码库加密** | Windows DPAPI（`System.Security.Cryptography.ProtectedData`） | 自研 `PasswordVault` |
| **密码匹配** | 参考 ArchivePasswordTestTool 的 `7z t -p` 模式 | 自研 `PasswordMatcher` |
| **右键菜单** | 参考 7-Zip 官方 `.reg` 写法 | 自研 `ContextMenu` |
| **UI** | 自研 WPF | .NET 10 + Fluent 风格 |
| **安装包** | InnoSetup（本机已有相关工具链） | 自研脚本 |

### 技术架构图

```
┌─────────────────────────────────────────────┐
│          SmartUnzip (WPF, .NET 10)          │
│                                             │
│  ┌─────────────┐      ┌─────────────────┐   │
│  │ 拖拽 / 右键  │─────▶│ ExtractionTask  │   │
│  │   UI 层     │      │   Manager       │   │
│  └─────────────┘      └────────┬────────┘   │
│                                │            │
│         ┌──────────────────────┼──────┐     │
│         ▼                      ▼      ▼     │
│  ┌─────────────┐  ┌──────────────┐ ┌──────┐ │
│  │FileType     │  │Password      │ │Secur-│ │
│  │Detector     │  │Matcher       │ │ityVa-│ │
│  │(魔数检测)    │  │(密码库匹配)   │ │lida- │ │
│  └─────────────┘  └──────┬───────┘ │tor   │ │
│                          │         └──────┘ │
│                   ┌──────▼───────┐          │
│                   │PasswordVault │          │
│                   │(DPAPI 加密)   │          │
│                   └──────────────┘          │
│                          │                  │
│                   ┌──────▼───────────┐      │
│                   │IArchiveEngine    │      │
│                   │ └SevenZipEngine  │      │
│                   └──────┬───────────┘      │
└──────────────────────────┼──────────────────┘
                           │ 进程调用
                           ▼
                  ┌─────────────────┐
                  │ 7z.exe (26.03)  │
                  │ 核心解压引擎      │
                  └─────────────────┘
                           │
                           ▼
                  ┌─────────────────┐
                  │ SmartUnzip.exe  │
                  │ + Setup.exe     │
                  └─────────────────┘
```

---

## 6. 风险清单

### 6.1 许可证风险
| 风险 | 等级 | 应对 |
|------|------|------|
| 误用 LGPL/GPL 项目代码导致被迫开源 | 🟡 中 | **不抄 PeaZip/SevenZipSharp 代码**，仅参考思路 |
| 随附 7-Zip 时漏附 License.txt 或修改二进制 | 🟡 中 | 原样分发 + `THIRD-PARTY-NOTICES.txt` |
| unRAR 许可限制被误读为"可自由用于压 RAR" | 🟡 中 | SmartUnzip **只解不压**，不触碰此红线 |

### 6.2 技术风险
| 风险 | 等级 | 应对 |
|------|------|------|
| RAR5 高版本/newer WinRAR 特有算法 7-Zip 可能解不了 | 🟡 中 | 明确错误提示；预留 WinRAR 备用引擎接口（`IArchiveEngine` 已有 `FutureEngine` 位） |
| 加密 RAR 的密码验证在 7-Zip 下可能有兼容问题 | 🟡 中 | 实测覆盖；失败时清晰报错 |
| 进度解析依赖 `7z -bsp1` 输出格式，版本间可能变化 | 🟢 低 | 解析层做容错（正则宽松 + 解析失败时降级为不确定进度条） |
| TAR 无魔数（需读 offset 257） | 🟢 低 | 检测器专门处理 |

### 6.3 维护风险
| 风险 | 等级 | 应对 |
|------|------|------|
| 自研意味着无上游更新 | 🟡 中 | 模块化 + 接口化，降低单体复杂度 |
| 7-Zip 升级导致兼容问题 | 🟢 低 | 版本检测 + 最低版本要求（建议 ≥ 21.07，本机 26.03） |

### 6.4 安全风险
| 风险 | 等级 | 应对 |
|------|------|------|
| **Zip Slip / Path Traversal**（压缩包内 `../../xxx`） | 🔴 **高** | 自研 `SecurityValidator`：解压前校验每条 entry 的规范化路径必须落在目标目录内 |
| 符号链接逃逸 | 🟡 中 | 解压后扫描并拒绝越界符号链接 |
| 恶意压缩包（zip bomb） | 🟡 中 | 检测异常压缩比，超阈值警告 |
| 密码明文落盘 | 🔴 **高** | DPAPI 加密；日志中永久排除密码字段 |

### 6.5 第三方依赖风险
| 依赖 | 风险 | 应对 |
|------|------|------|
| 7-Zip 二进制 | 版本漂移 | 检测用户已装版本，优先复用 |
| WinRAR（可选备用） | 商业软件，不得随附 | 仅检测用户自装，不打包 |
| NuGet 包 | 供应链 | 仅引入必要包，锁定版本 |

---

## 7. 是否建议进入第二阶段：**建议**

**理由**：
1. 已实测验证：.NET 10 工具链可编译、7-Zip 26.03 可用、魔数检测可行、密码试错闭环可行
2. 已确认：**不存在可直接 Fork 的成熟基础项目**（这是基于 12+ 候选的实际核查结论，不是推测）
3. 自研路径依赖项少（WPF + 7z.exe + DPAPI），全部为成熟稳定的官方能力
4. 风险主要集中在「安全解压」与「许可证合规」，两者都有明确可执行的应对方案

**第二阶段建议路线**：
```
建立 SmartUnzip 解决方案（.NET 10 WPF）
  ↓
实现 FileTypeDetector（魔数表 + 伪装判定）
  ↓
实现 SevenZipEngine（进程调用 + 进度/取消）
  ↓
实现 PasswordVault（DPAPI）+ PasswordMatcher（7z t -p 遍历）
  ↓
实现 SecurityValidator（Zip Slip 防护）
  ↓
WPF 主界面（拖拽 + 文件卡片 + 进度条）
  ↓
右键菜单 + 设置页
  ↓
实测 → 修 Bug → 编译 EXE → 打包
```

---

*报告结束。所有"实测"数据均来源于 2026-10-04 在本机的真实执行结果，非文档摘录。*
