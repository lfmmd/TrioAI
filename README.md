# TrioAI — MotionPerfect AI 助手插件

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Version](https://img.shields.io/badge/version-0.1-blue.svg)](https://github.com/lfmmd/TrioAI/releases)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-blue.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg)](https://www.microsoft.com/windows)
[![MotionPerfect](https://img.shields.io/badge/MotionPerfect-V5.6+-orange.svg)](https://www.triomotion.com/)
[![API: Anthropic Messages](https://img.shields.io/badge/API-Anthropic%20Messages%20%2Fv1%2Fmessages-ff6b6b.svg)](https://docs.anthropic.com/en/api/messages)
[![GitHub Release](https://img.shields.io/github/v/release/lfmmd/TrioAI?include_prereleases)](https://github.com/lfmmd/TrioAI/releases)
[![GitHub stars](https://img.shields.io/github/stars/lfmmd/TrioAI?style=social)](https://github.com/lfmmd/TrioAI/stargazers)

为 Trio MotionPerfect 运动控制器编程 IDE 提供 AI 编程助手能力的插件。AI 通过工具调用（tool use）直接读写程序、操作控制器、查询 TrioBASIC 命令参考，让自然语言驱动的运动控制开发成为可能。

## 特性

- **嵌入式 AI 助手面板**：在 MotionPerfect 内嵌对话式 AI 助手，原生 UI，无外部依赖
- **24 个 AI 工具**：覆盖程序读写、控制器交互、VR/TABLE 数据、TrioBASIC 命令查询等
- **HTTP API 服务器（MCP 风格集成端点）**：内置 `localhost:9090` HTTP API，提供类似 [MCP（Model Context Protocol）](https://modelcontextprotocol.io)的工具接入能力 —— 让外部 AI 应用、自动化脚本、其他 LLM agent（如 Claude Desktop、Cursor、自研 client）能通过标准 HTTP 接口调用 MotionPerfect 的全部功能（程序读写、控制器交互、VR/TABLE 数据等）
- **API 兼容格式**：[Anthropic Messages API](https://docs.anthropic.com/en/api/messages)（端点 `/v1/messages`，请求体/响应/SSE 流式事件均遵循 Anthropic 规范）。**不兼容 OpenAI Chat Completions 格式**。支持智谱 GLM（`GLM-5.1`、`GLM-5`）、DeepSeek、Anthropic 官方、以及任何兼容 Anthropic Messages API 的代理或第三方服务
- **流式响应**：实时显示 AI 思考与工具调用过程
- **二次确认机制**：所有破坏性操作（写、删除、运行、停止）需用户在 UI 中点确认
- **TrioBASIC 严格语法约束**：内置命令参考库，AI 写代码前自动查证，避免幻觉
- **多语言 UI**：中文、英文、德文、法文

## 安装

### 方法 1：通过 .MPPlugin 包安装

1. 下载 `TrioAI.MPPlugin`
2. 在 MotionPerfect 中：工具 → 插件管理 → 安装插件 → 选择 `TrioAI.MPPlugin`
3. 重启 MotionPerfect

### 方法 2：手动部署

1. 解压 `TrioAI.MPPlugin`（实质是 ZIP）到 MotionPerfect 的插件目录：
   ```
   %LOCALAPPDATA%\TrioMotion\MotionPerfectV5.6\Plugins\TrioAI\
   ```
   解压后结构：
   ```
   TrioAI\
     ├─ TrioAI.MPPlugIn.dll
     └─ skills\triobasic\
          ├─ index.json
          └─ skills.json
   ```
2. 重启 MotionPerfect

### 配置 API Key

启动 MotionPerfect 后，AI 助手面板顶部点击设置图标，填入：

| 字段 | 说明 |
|------|------|
| **API Key** | API Key（去对应平台控制台申请） |
| **API URL** | Base URL（无需手动补 `/v1/messages`，代码会自动拼接） |
| **Model** | 平台支持的模型名 |

#### 兼容的 API 服务

| 平台 | API URL（base_url） | 模型示例 | 申请入口 |
|------|---------------------|----------|----------|
| **智谱 GLM** | `https://open.bigmodel.cn/api/anthropic` | `GLM-5.1`、`GLM-5`、`GLM-4.6` | https://bigmodel.cn |
| **DeepSeek** | `https://api.deepseek.com/anthropic` | `deepseek-v4-pro`、`deepseek-v4-flash`、`deepseek-chat`、`deepseek-reasoner` | https://platform.deepseek.com |
| **Anthropic 官方** | `https://api.anthropic.com` | `claude-sonnet-4-5`、`claude-opus-4-7`、`claude-haiku-4-5` | https://console.anthropic.com |
| 任意 Anthropic 兼容代理 | 你的代理 base URL | 代理支持的模型名 | 代理服务商 |

**为什么是 Anthropic 格式不是 OpenAI？**

Anthropic Messages API 原生支持**工具调用（tool use）**+ **流式响应（SSE）**+ **思维链（thinking）**，这三者对 AI 控制器编程这种需要工具循环的场景更原生。OpenAI 的 `chat/completions` 工具调用规范虽然也支持，但事件结构和参数约定不同，本项目未实现。

如果你只有 OpenAI 兼容的 API，可以考虑用 [LiteLLM](https://github.com/BerriAI/litellm) 这类代理把它转成 Anthropic 格式再接入。

配置文件位于：`%APPDATA%\TrioAI\config.json`

### Skill 数据初始化（首次使用必读）

TrioAI 内置约 1 MB 的 TrioBASIC 命令参考库，AI 通过 `lookup_command` 工具实时查证命令语法、避免幻觉。**这些数据需要在使用前手动初始化一次**：

1. 打开 AI 助手面板 → 点顶部设置图标
2. 找到 **「初始化 Skill 数据」** 按钮（仅未初始化时显示，橙色）
3. 点击 → 等待几秒，弹出「初始化完成」提示
4. 按钮自动隐藏

**初始化做了什么：**
- 把插件目录下的 `skills\triobasic\`（`index.json` + `skills.json`，共约 1 MB）复制到 `%APPDATA%\TrioAI\skills\`
- 部署 `AI_INSTRUCTIONS.md`（首次创建，之后用户修改会保留）
- 标记 `skillsInitialized=true` 写入 config.json

**不初始化会怎样：**
- AI 调用 `lookup_command` 时拿不到命令参考 → 写代码可能幻觉不存在的 TrioBASIC 命令
- 不会崩溃，但代码质量明显下降

**重新初始化（恢复默认）：**
- 在设置面板再次点「初始化 Skill 数据」（按钮显示后）
- 或删除 `%APPDATA%\TrioAI\skills\` 目录后重启 MP

**.MPPlugin 升级到新版本后**：插件会自动用新版 `skills/` 覆盖本地数据（保留你的 `AI_INSTRUCTIONS.md` 自定义）。

## 使用

### AI 对话

打开 MotionPerfect 后，左侧或工具菜单找到 **AI 助手** 面板，输入问题即可。

**示例对话：**

- *"帮我写一个循环检测 VR(512) 的程序，当值为 1 时打印 hello"*
- *"列出当前项目所有程序"*
- *"读 MAIN1 的源码并解释它的功能"*
- *"查一下 MOVE 和 MOVECIRC 命令的语法区别"*
- *"编译并运行 AXIS_TEST 程序"*

### 工具确认

当 AI 要执行破坏性操作（写程序、写 VR、运行/停止程序、删除程序等），底部会弹出确认面板：

- **允许**（左侧绿色）：执行操作
- **拒绝**（左侧红色）：取消操作

按钮位置在面板左侧，远离底部的「发送/停止」按钮，避免误触。

### 输入框快捷键

- **Enter**：发送消息
- **Shift + Enter**：换行

## AI 工具列表

> 共 24 个工具。详见 [API.md](API.md)。

**程序管理**
- `list_programs` — 列出项目所有程序
- `create_program` — 创建程序（需确认）
- `delete_program` — 删除程序（需确认）
- `read_source` — 读程序源码
- `write_source` — 写完整源码（需确认）
- `patch_source` — 行级编辑（需确认）
- `open_program` — 在编辑器打开程序
- `search_code` — 跨程序搜索代码

**编译运行**
- `compile_program` — 编译程序
- `run_program` — 运行程序（需确认）
- `stop_program` — 停止程序（需确认）
- `upload` — 上传到控制器（需确认）
- `download` — 从控制器下载
- `get_program_process` / `set_program_process` — 进程/自启设置

**控制器数据**
- `get_status` — 控制器连接状态、产品名、固件版本
- `list_axes` — 列出所有轴配置
- `read_vr` / `write_vr` — 读/写 VR 变量
- `read_table` / `write_table` — 读/写 TABLE 数据

**项目**
- `save_project` — 保存当前项目

**知识库**
- `lookup_command` — 查询 TrioBASIC 命令/语法参考
- `list_descriptors` — 列出程序类型描述符

## HTTP API

插件内置 HTTP 服务器，监听 `http://localhost:9090`。可被外部 Python、Node.js、curl 脚本调用，实现自动化。

### 为什么是「MCP 风格」而非真正的 MCP？

项目的演进路径：

1. **第一版思路**：做一套「外部 AI 工具通过 curl 操作 MotionPerfect」的 HTTP 接口 —— 类似 [MCP（Model Context Protocol）](https://modelcontextprotocol.io) 的理念：把 IDE 的能力暴露成标准接口，让任意 AI client 能接入
2. **意识到的问题**：这种方式需要用户自己在外部 AI 助手（Claude Desktop、Cursor、Claude Code 等）和 MotionPerfect 之间手动切换，工作流被割裂 —— 而且如果走这条路，用户完全可以直接用 Claude Code 这类成熟的 agent，没必要再做一个 wrapper
3. **重构**：把 AI 助手直接嵌入 MotionPerfect —— 同一个对话面板里调用所有工具，无缝集成在编程 IDE 内

**所以当前的 HTTP API 是第一版架构的「遗产」**，技术上不是严格的 MCP 实现（MCP 用 JSON-RPC，我们用 REST），但保留了「让外部脚本/agent 通过标准接口调用 IDE」的能力：

- ✅ 仍然适合自动化测试、CI 集成、批处理脚本
- ✅ 仍然适合让外部 AI client 调用（虽然不是 MCP 协议，但接口稳定可用）
- ❌ 不适合作为「Claude Desktop 等 MCP-native 应用的直接连接端点」（需要写一层 MCP→HTTP 适配器）

### 快速示例

```bash
# 获取控制器状态
curl http://localhost:9090/api/status

# 列出所有程序
curl http://localhost:9090/api/programs

# 读 MAIN1 源码
curl http://localhost:9090/api/programs/MAIN1/source

# 读 VR(0) 起的 10 个值
curl "http://localhost:9090/api/vr/0?count=10"

# 写 VR(0) = 100
curl -X PUT http://localhost:9090/api/vr/0 \
     -H "Content-Type: application/json" \
     -d '{"value": 100}'

# 编译 MAIN1
curl -X POST http://localhost:9090/api/programs/MAIN1/compile
```

> 完整端点列表见 [API.md](API.md)。

## 安全机制

### AI 行为约束

AI 系统提示词中强制要求：

1. **严禁生成 LOCK 类命令**（LOCK、LOCK AXIS、LOCK ALL）— 这些命令会锁死控制器
2. **严禁禁用轴驱动、抱闸、安全机制**
3. **严禁幻觉 TrioBASIC 语法** — 所有命令/关键字必须能在 `lookup_command` 中查到
4. **严禁使用其他 BASIC 方言**（VB、VB.NET、QBasic、FreeBASIC 等）的语法

### 二次确认

以下操作必须在 UI 中由用户点「允许」才能执行：

- `write_source` / `patch_source` — 写代码
- `write_vr` / `write_table` — 写控制器数据
- `create_program` / `delete_program` — 增删程序
- `run_program` / `stop_program` — 运行控制
- `upload` — 上传到控制器
- `set_program_process` — 修改进程设置

### 自动备份

所有写程序操作（`write_source` / `patch_source`）在写入前自动备份原文件到 `%APPDATA%\TrioAI\backup\`。

## 项目结构

```
TrioAI\
  ├─ TrioAIPlugIn.cs        # MP 插件入口（OnInitialize / OnDispose）
  ├─ AiService.cs           # AI 调用循环、工具定义、系统提示词
  ├─ Handlers.cs            # 24 个工具的具体实现
  ├─ ApiServer.cs           # HTTP API 服务器
  ├─ ChatPanel.cs           # WPF UI（对话面板、设置面板、多语言）
  ├─ DispatcherHelper.cs    # UI 线程辅助
  ├─ skills\triobasic\      # TrioBASIC 命令参考数据
  │    ├─ index.json        # 按需加载的索引（128KB）
  │    └─ skills.json       # 完整命令库（918KB）
  └─ TrioAI.MPPlugIn.csproj # .NET Framework 4.8 类库
```

## 自定义 AI 提示词

AI 助手使用的系统提示词定义在 `AiService.cs` 的 `DefaultPrompt` 字符串常量中。

**运行时位置：** `%APPDATA%\TrioAI\AI_INSTRUCTIONS.md`

**自定义方式（任选其一）：**

1. **直接编辑文件**（推荐用于个人定制）
   - 文件不存在时，插件首次启动会从代码里的 `DefaultPrompt` 创建它
   - 之后用户的修改会保留 —— MP 重启不会被覆盖
   - 想恢复默认：删除该文件，下次启动 MP 会重新生成

2. **改源码（推荐用于发布）**
   - 编辑 `AiService.cs` 中的 `DefaultPrompt`
   - 重新编译 `dotnet build -c Release`
   - 老用户需要手动删除 `%APPDATA%\TrioAI\AI_INSTRUCTIONS.md` 才会拿到新版提示词
   - 或在 UI 点击「初始化 Skills」按钮强制重置

## 构建

需要：
- .NET Framework 4.8 SDK
- MotionPerfect 已安装（依赖 `TrioSharedLibrary.dll`、`TrioCommunicationsLibrary.dll`、`TrioBaseLib.dll`，需放在项目父目录）

```bash
cd TrioAI
dotnet build -c Release
# 输出：bin\Release\TrioAI.MPPlugIn.dll
```

打包为 `.MPPlugin`：

```bash
python pack.py
# 输出：TrioAI.MPPlugin（ZIP 格式）
```

## 兼容性

- MotionPerfect V5.6+
- .NET Framework 4.8
- Windows 10 / 11

## 优化方向

以下是已知待优化项，按优先级排列：

- **regex 硬拦 VB 模式**：当前通过 `lookup_command` 白名单 + 签名解析（参数个数、可赋值性）阻止 AI 写不存在的 TrioBASIC 命令。下一步可以叠加 regex 黑名单，直接拦死典型 VB/QBasic 写法（`Dim`、`Function ... End Function`、`Class`、`Math.`、`Console.` 等），无需依赖白名单覆盖。目前 regex 规则准确率不足，暂未启用。
- **IEC 断点的 line→CodeElement 反推**：目前 IEC 程序的断点需要用户在 MP UI 中手动设置，AI 调用 `set_breakpoint` 会返回明确错误。
- **HTTP API 鉴权**：当前 HTTP API 监听 `localhost:9090` 但无鉴权，理论上同机其他进程可调用。生产环境建议加 token 鉴权或仅绑定 loopback。
- **多控制器切换**：目前插件绑定单一控制器，多控制器项目场景需要手动切换。
- **国际化扩展**：UI 已支持中/英/德/法，可继续扩展。
- **程序执行历史/审计日志**：所有 AI 写操作目前只有自动备份，没有结构化审计日志。

## 许可证

[MIT License](LICENSE)
