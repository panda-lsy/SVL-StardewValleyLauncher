# Nexus Mods 与 CurseForge MCP 调试说明

SVL 的在线资源解析和整合包来源校验可以使用两个 MCP 辅助调试。MCP 只用于开发期的只读查询与问题复现，不替代 SVL 自身的下载、缓存和安装逻辑。

## 已配置的服务

| 服务 | 仓库 | 固定版本 | 环境变量 |
| --- | --- | --- | --- |
| CurseForge | `https://github.com/UberMorgott/curseforge-mcp-server` | `38445d9cac91e61430c82b63d752c12c78badfce` | `CURSEFORGE_API_KEY` |
| Nexus Mods | `https://github.com/mbj733/nexus-mcp` | `8cc9bd9a20518afd105ee81ea469b139f1a7b3af` | `NEXUS_MODS_API_KEY` |

配置位于开发机用户级 Codex 配置文件中，不提交到仓库：

```text
C:\Users\<用户名>\.codex\config.toml
```

两个 API Key 也必须只配置为开发机用户环境变量，不能写入 `config.toml`、项目文件、日志或提交记录。当前项目配置仅声明变量名，因此缺少 Key 时 CurseForge 只提供有限的零配置查询能力，Nexus Mods 则需要 API Key 或本地登录 Cookie。

## 初始化与重启

在 PowerShell 中按用户范围配置变量（将值替换为自己的 Key；不要把命令写入项目文档或提交记录）：

```powershell
[Environment]::SetEnvironmentVariable('CURSEFORGE_API_KEY', '<CurseForge API Key>', 'User')
[Environment]::SetEnvironmentVariable('NEXUS_MODS_API_KEY', '<Nexus Mods API Key>', 'User')
```

设置后必须完全重启 Codex，使 MCP 进程继承新的环境变量。不要在聊天、Issue 或日志中回显 Key；排查时只检查“变量是否存在”，不要打印变量值。

如果不希望配置 Nexus API Key，可以在 MCP 仓库目录执行一次登录辅助脚本：

```powershell
node scripts/login.mjs
```

登录成功后 Cookie 会保存在用户目录的 `.nexus-mcp-cookies.json`，MCP 会自动读取。Cookie 与 API Key 具有同等敏感性，也不能提交或回显。

## 推荐用途

- CurseForge：核对项目 ID、文件 ID、游戏版本和最新文件，辅助验证 `svl-source.json` 与更新判断。
- Nexus Mods：核对 Nexus 项目、文件和集合信息，辅助验证 NXM/来源链和整合包条目。
- 真实下载仍应通过 SVL 下载器完成，以便同时覆盖缓存命中、校验、重试、进度和备份流程。

调试来源信息时，应优先按“父 Mod → 嵌套子 Mod”的关系记录来源。子 Mod 没有独立平台项目或文件 ID 时，不要伪造 ID；应保留父 Mod 来源和相对路径/子 Mod 标识。

## 安全与复现约定

1. 默认使用只读查询，不通过 MCP 修改远端资源。
2. 记录项目 ID、文件 ID、版本和错误类型即可，不记录 API Key、完整授权响应或不必要的个人信息。
3. 每次线上问题复现应同时记录 SVL 任务日志、缓存文件名、`manifest.json` 和 `svl-source.json` 的关键字段，便于区分平台解析错误、压缩包嵌套结构错误和本地替换错误。
4. MCP 服务升级时先更新固定 commit，完成 MCP 查询回归后再改版本；不要直接使用未固定的 `main`。
