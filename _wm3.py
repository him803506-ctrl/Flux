import io

p = r"C:\Users\HKN\WorkBuddy\2026-10-04-12-27-25\.workbuddy\memory\2026-10-04.md"

text = """

## 打包到 GitHub（2026-10-04 22:45）—— 本地已就绪，卡在授权

### 目标仓库
- owner: `him803506-ctrl`（天才大母鸡 / him803506@gmail.com）
- 仓库名：**`Flux`**（该名字未被占用）
- 可见性：**public**（与用户现有两个仓库 `qq-nt-chat-decrypt` / `QQ-tool` 一致，
  它们都是 public 且都写了 description）

### 打通 GitHub 的两条路（都不是走连接器）

**① GitHub 连接器（mcp__github__*）—— 建仓库失败**
```
POST https://api.github.com/user/repos
403 Resource not accessible by integration
```
连接器的 token 只有**读权限**，没有 `repo` scope，无法创建仓库。
`push_files` 虽有 `content` 参数，但**是字符串，二进制会损坏**，也不可用。

**② gh CLI（已装好）—— 可行，等用户授权**
- 下载：`https://ghfast.top/https://github.com/cli/cli/releases/download/v2.89.0/gh_2.89.0_windows_amd64.zip`
  （GitHub 主站和 api.github.com 都被墙，**只有 release 下载走镜像才通**）
- 解压：WinRAR **不能**解 zip，用 Python `zipfile`
- 位置：`C:\\ai\\aicode\\gh\\bin\\gh.exe`（版本 2.89.0）
- 版本探测技巧：镜像的 **API 代理不通**，但按 URL 格式
  `/releases/download/v<ver>/gh_<ver>_windows_amd64.zip` 直接 HEAD 探测可用。

**当前状态：两个 gh 进程在等设备码授权**
- pid 26064 设备码 `68B2-199A`
- pid 23676 设备码 `7EF8-4A9D`（较新，优先用这个）
- URL：https://github.com/login/device

### 本地仓库已完全就绪
- `git init` + 2 次提交（`f407c60`、`cdd12a6`），分支 `main`
- **64 个文件 / 1.47 MB**（从 479 MB 精简而来）
- 仓库级 git 身份：`天才大母鸡 / him803506@gmail.com`（未污染全局配置）

### 上传前的清理（都已完成并验证）

| 项 | 结果 |
|---|---|
| **证书私钥 `certs/Flux-Sparse.pfx`** | 被 `.gitignore` 拦住，**确认不在暂存区**（只提交了公钥 `.cer`） |
| `bin/ obj/ publish/` | 全部排除（占 307 MB） |
| `dist/`（含 127 MB 的 Flux.exe） | 排除（超 GitHub 单文件 100 MB 限制） |
| **本机绝对路径** | 3 个构建脚本里的 `C:\Users\HKN\...` 改为从脚本位置推导 |
| 证书密码明文 | `_make_cert.sh` 改为读 `FLUX_CERT_PASSWORD` 环境变量（base64 传给 PowerShell） |
| 用户可见旧名 | 全部改为 Flux（保留 namespace / 加密盐 / 配置目录三处，README 有说明） |

### 文档重构
- `README.md` → 重写为**开发者向**（GitHub 首页）：logo、截图、特性、技术栈、构建、目录结构
- 原 302 行用户文档 → `docs/使用说明.md`
- `docs/编译说明.md` → 更名 + **新增第 10 节**（IExplorerCommand / 稀疏包 / vtable 槽位表 / 排错对照表）
- 截图移到 `docs/images/`（main-window / context-menu / icon-sizes / logo）
- `LICENSE` 与 `THIRD-PARTY-NOTICES.txt` 从 `dist/` 复制到仓库根（否则 README 链接失效）
- 给 `docs/编译说明.md` 补了**锚点链接**用的章节标题

### 授权后的收尾命令
```bash
export PATH="/c/Program Files/Git/cmd:$PATH"
cd /c/Users/HKN/WorkBuddy/2026-10-04-12-27-25/SmartUnzip
gh auth setup-git                       # 让 git 用 gh 的凭据
gh repo create Flux --public --description "..." --source=. --remote=origin --push
```
"""

with io.open(p, "a", encoding="utf-8") as f:
    f.write(text)

print("written, total lines:", sum(1 for _ in io.open(p, encoding="utf-8")))
