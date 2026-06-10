# Changelog

本项目所有重要变更都记录在此文件中。

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

后续计划：
- 国际化扩展（更多语言）
- 程序执行历史/审计日志
- HTTP API 鉴权（防止其他进程误调用）
- 多控制器切换支持
- IEC 断点的 line→CodeElement 反推（目前 SetBreakpoint 需在 MP UI 中手动设置）
- regex 硬拦 VB 模式（`Dim`/`Function...End Function`/`Class`/`Math.`/`Console.`）—— 目前规则准确率不足，暂未启用

## [0.1.9] — 2026-06-10

TrioBASIC 写入前白名单 + 签名校验版本。

### 新增

- **Phase 1：标识符白名单校验** — `write_source` / `patch_source` 在写入前调用 `ValidateTrioBasicCode`，扫描代码中所有 `Name(...)` 调用形式与 `[A-Z_]+` 全大写标识符，凡是不在以下三类中的就拦截：(1) TrioBASIC 内置关键字（IF/FOR/WHILE/DIM 等）；(2) `lookup_command` 索引（806 条 + 180 个 HTML 补充条目）；(3) 用户在当前项目里已经声明的变量。AI 写出 `Foobar(...)` 这种幻觉命令会立即得到 `BLOCKED by TrioBASIC validation: Unknown: ['FOOBAR']`，强制回查 `lookup_command`。
- **Phase 2：签名解析 + 参数校验** — 从 `index.json` 的 desc 字段解析出每个命令的最小/最大参数个数与是否可赋值（`x = ABS(...)` 合法，`SIN(...) = 0` 不合法），匹配 3 种签名模式：`value = NAME(...)`、`NAME(...)`、`NAME arg1, arg2`。参数超界/不足立即拦截（如 `ABS(1, 2)` 拦截：`got 2, max 1`）。
- **README「优化方向」章节** — 列出已识别但未实施的 6 项待优化（含 regex 硬拦 VB 模式为何暂缓）。

### 设计取舍

- **白名单优先，黑名单暂缓** — `lookup_command` 白名单 + 签名校验比 regex 黑名单更稳：白名单基于真实命令库（CHM 解析而来），覆盖准确；regex 黑名单要枚举 VB/QBasic 写法，规则越多越容易误伤（比如 `Dim` 在 TrioBASIC 中也合法）。先做白名单拦截，regex 待规则打磨稳定后再叠加。
- **只拦截写操作，不拦截读** — 校验只在 `write_source` / `patch_source` 入口执行；`read_source` / `search_code` 不校验。AI 在思考阶段可以自由探索代码（包括读用户写的有问题的代码），只在「要落到磁盘」这一步强制约束。
- **用户变量白名单动态构建** — 校验时遍历当前项目所有程序的赋值左侧，提取用户变量名加入白名单。这样 AI 写代码引用其他程序里的全局变量不会被误拦。
- **错误信息可操作** — 拦截时返回 `BLOCKED by TrioBASIC validation:` + 每行一个具体原因（`Unknown: ['FOOBAR']` / `L1: ABS got 2, max 1`），AI 能直接据此修正后重试。

## [0.1.8] — 2026-06-10

TrioBASIC 方言混淆防御强化版。

### 改进（AI_INSTRUCTIONS.md / DefaultPrompt）

- **方言约束提到 system prompt 顶部** — 之前 `STRICT TRIOBASIC SYNTAX COMPLIANCE` 在中段，长对话时被淡化。现在紧跟 `## Capabilities` 之后，确保 AI 进入工作状态前先读到。
- **加 22 行 few-shot 正反对照表** — 之前只列反例（`Dim`、`Function...End Function`），LLM 训练数据里 VB/QBasic 写法远多于 TrioBASIC，光说"不要这样"不够。现在每行明确 `WRONG (other BASIC) → CORRECT (TrioBASIC)` 对照，覆盖变量声明/函数定义/控制流/异常/IO/数学/类型注解/比较运算符/注释等全部常见混淆点。
- **加 AFTER-WRITE SELF-CHECK 强制自检流程** — 提示词之前只说"MANDATORY before writing"，但 AI 写完后没反向校验环节。现在加 5 步自检：(1) 列出所有命令 (2) 每个判断是否查过 (3) 没查的立即查 (4) 查不到不提交 (5) 对照表格复查模式。
- **移除中段重复的 `STRICT TRIOBASIC` 和 `confusions` 章节** — 现在约束集中到顶部，避免分散注意力。

注：DeployAIInstructions 每次启动会 overwrite `%APPDATA%\TrioAI\AI_INSTRUCTIONS.md`，用户下次启动 MP 自动获得新约束，无需手动同步。

## [0.1.7] — 2026-06-10

代码质量回归修复版本（v0.1.5 的 token 优化过于激进）。

### 修复

- **MaxToolResultLen 8000 → 16000** — 8000 截断了 11% 的 HTML 命令文档（共 98 个 > 8KB），其中全是高频复杂命令（FRAME 119KB, ETHERCAT 51KB, REGIST 47KB, MS_BUS 47KB, MODBUS 38KB, CAMBOX 35KB, PRINT 33KB）。截断后 AI 只看到命令简介，参数表/示例全丢，凭印象写参数 → 编译错。
- **microCompact 永不清空 lookup_command 结果** — 之前保留最近 5 个 tool_result，但复杂程序常用 10+ 个命令，5 个之外的语法被清空成 `[Old tool result content cleared]`。AI 写代码时找不到精确语法，且常被迫重复查询浪费 API。现在 lookup_command 的 tool_result 永久保留，仅清空 Read 大文件/WebFetch 等一次性结果。

## [0.1.6] — 2026-06-10

safe-coding 强制嵌入版本。

### 修复

- **AI 写 TrioBASIC 代码不遵守 safe-coding 规范** — markdown skill 之前只在 system prompt 列名字+描述，没有 MANDATORY 触发语；AI 不会主动 `read_skill('safe-coding')`，靠训练记忆硬写。即使某轮读了，microCompact 5 轮后也会清空。现在 `BuildSkillsCatalog` 直接把 safe-coding 全文嵌入 system prompt（~200 token），每轮可见，永不被清空。

## [0.1.5] — 2026-06-10

Token 优化 + IEC 稳定性版本。

### 新增

- **microCompact 工具结果生命周期管理** — 旧 tool_result 的 content 自动清空（保留 tool_use_id 不破坏配对），保留最近 5 个完整内容。预计节省 30-60% 请求 token。
- **token 估算触发裁剪** — `TrimHistory` 改用 chars/4 估算（30k 阈值）+ 条数兜底，而非单纯按消息条数。30 条纯对话仅 5k token，但 5 个 lookup_command 就 20k+。
- **HTML 参考库（IEC/PLCopen）** — `lookup_command` 工具覆盖 IEC 61131-3 和 PLCopen 全部命令/功能块，AI 写代码前主动校验。
- **prompt 缓存标记** — system prompt + tools + 最后 assistant 消息打 `cache_control`（GLM 走隐式缓存，标记无害；切换到 Anthropic 端点会生效）。
- **智能截断** — HTML heading/table 边界处切，避免语法表被截成半句。

### 修复

- **IEC ST 局部变量写入静默失败** — LLM 输出 LF 行尾，但 MP 的 `STCodeGenerator.SplitCode` 内部按 `"VAR\r\n"` 匹配，必须 CRLF。在 `WriteIecSource` / `WriteIecVariables` 入口强制规范化。
- **IEC 新建 POU 不在项目树显示** — `AddNewProgram` 的 folder 参数不能为 null，否则 `IECObjectPOU` 构造函数的 `Folder?.Add(this)` 跳过注册。改为传 `EnsureDefaultProgramFolder(false, false)`。
- **IEC 自动创建 POU 总是追加到第一个现有 POU** — `EnsureIecPou` 不论 pouName 是否传入都返回 `TryGetFirstIecPou`。改为按 pouName 匹配，找不到才创建。

### 调整

- `MaxToolResultLen` 16000 → 8000（约 2000 token 上限，单条节省 50%）。
- `BuildSkillsCatalog` 去掉每库 5 个命令示例，只列库名+条目数。
- `BuildProjectContext` 程序列表改为数量+类型分布，不再列每个名字。
- `max_tokens` 自动升级 8K → 64K（处理大程序生成）。

## [0.1.2] — 2026-06-10

控制器深度集成 + IEC 端到端支持版本。

### 新增

- **27 个 HTTP 路由 / AI 工具**，覆盖控制器深度操作：
  - 轴状态/详情（`isActive` / `isInError` / `AxisStatus` / `DriveStatus`）
  - 系统变量读写（`/sysvars`、`/sysvar/{name}`）
  - 数字/模拟 IO 读写（`/io/digital`、`/io/analogue`）
  - 进程列表 + 运行中变量读（`/processes`、`/processes/{pid}/variables`）
  - 控制器事件订阅（`/events`）
  - 驱动器参数（`/drive/{axis}/{addr}`）
  - EtherCAT 设备扫描 + SDO 读写（`/ethercat/devices`、`/ethercat/sdo`）
  - MS Bus 模块扫描（`/msbus/scan`）
  - 远程设备 / 机器人 / 配方 / 报警 列表
  - 示波器打开（`/oscilloscope/open`）
  - 项目项列表 / 打开项目（`/project/items`、`/project/open`）
  - 插件探测（`/plugins`）
  - 程序复制（`/programs/{name}/copy`）

### 修复

- **IEC 程序完整集成**：`compile` / `run` / `stop` / `upload` / `open` / `breakpoints` 列表全部通过反射调用 `ContainerTask` 公开方法实现。`run` 内部自动 Compile + DebugManager.Start。
- **IEC ST 编译报错 "VAR: 缺少新语句"**：根因是 `STCodeGenerator` 类的实际命名空间是 `Trio.PlugIns.IEC61131_3.Models`（不是 `CodeGenerators`），`asm.GetType()` 返回 null，导致 `SplitCode` 静默 no-op，VAR...END_VAR 块被写入 .src 文件触发语法错误。改用 `asm.GetTypes().FirstOrDefault(t => t.Name == "STCodeGenerator")` 按类名查找。
- **IEC 程序复制**：改用 `CreateAndAddItem` + source-copy，绕过对 IEC 容器不适用的 `proj.CopyItem` 文件路径操作。
- **search_code 路由**：补充进 `ApiServer.cs`（之前完全缺失，返回 404）。
- **空 task 读取 IEC 源码**：返回空字符串而非抛 "IEC item has no POU"。
- **11 个新路由的 segments.Length 偏差 1 bug**（io、plugins、robots、recipes、alarms、remote-devices、msbus、ethercat、drive、processes、oscilloscope 全部受影响）。

### 已知限制

- IEC 断点的 line→CodeElement 反推未实现，`POST /programs/{name}/breakpoint` 对 IEC 返回明确错误（`line→CodeElement` 反推需要 IEC 解析器集成）。请用 MP UI 设置 IEC 断点。
- IEC `MAIN` 类型 POU 不支持 `VAR_INPUT` / `VAR_OUTPUT`（语义限制，需用 SubProgram 或 UDFB 类型）。

## [0.1.1] — 2026-06-09

bug 修复版本。

### 修复

- **TrimHistory 边界 bug**：当用户单次输入触发多个连续工具调用时，最近窗口里可能没有任何 plain-text user 消息（全是 assistant/tool_use/tool_result 对）。旧逻辑会让搜索循环跑到列表末尾，只保留最后一条消息（通常是 `user(tool_result)`），导致对应的 `tool_use` 孤立 → API BadRequest: `tool_use_id found in tool_result blocks`。新逻辑在找不到合适裁剪点时**跳过本次裁剪**，保留全部历史 —— 临时 token 超限远比 BadRequest 容易处理。

### 文档

- README 加入智谱 GLM（`GLM-5.1`、`GLM-5`）的 Anthropic 兼容端点配置说明
- README 加入「Skill 数据初始化（首次使用必读）」小节
- README 加入「为什么是 MCP 风格而非真正的 MCP」项目演进说明
- 顶部加入 8 个 badges（license / version / .NET / platform / MotionPerfect / API 格式 / Release / stars）



## [0.1] — 2026-06-08

首个公开版本。

### 新增

- **AI 助手面板**：MotionPerfect 内嵌的对话式 AI 助手，原生 WPF UI
- **24 个 AI 工具**：
  - 程序管理：`list_programs` `create_program` `delete_program` `read_source` `write_source` `patch_source` `open_program` `search_code`
  - 编译运行：`compile_program` `run_program` `stop_program` `upload` `download`
  - 进程设置：`get_program_process` `set_program_process`
  - 控制器数据：`get_status` `list_axes` `read_vr` `write_vr` `read_table` `write_table`
  - 项目：`save_project`
  - 知识库：`lookup_command` `list_descriptors`
- **HTTP API 服务器**（`http://localhost:9090`）：25 个 REST 端点，可被外部 Python/Node.js/curl 脚本调用
- **Anthropic Messages API 兼容**：支持 DeepSeek、Anthropic 官方、任意兼容代理
- **流式响应**：实时显示 AI 思考和工具调用过程
- **二次确认机制**：写程序、写 VR、运行/停止等 9 类破坏性操作需用户点「允许」
- **自动备份**：所有写程序操作前自动备份原文件到 `%APPDATA%\TrioAI\backup\`
- **TrioBASIC 严格语法约束**：
  - LOCK 类命令拦截（防止锁死控制器）
  - 禁止幻觉 TrioBASIC 命令（写代码前 `lookup_command` 查证）
  - 禁止其他 BASIC 方言语法（VB、VB.NET、QBasic 等）
  - 禁止变量名与系统保留名冲突（大小写不敏感）
- **多语言 UI**：中文、英文、德文、法文
- **Skills 命令库**：内置 918KB 的 TrioBASIC 官方命令参考，按需加载索引
- **配置 UI**：API Key / URL / Model 设置，文件位于 `%APPDATA%\TrioAI\config.json`
- **历史记录裁剪**：超过 30 条自动裁剪，避免 token 超限
- **打包脚本** (`pack.py`)：一键生成 `.MPPlugin` 包

### 安全

- LOCK 类命令硬拦截
- AI 系统提示词强制安全规则
- 破坏性操作 UI 二次确认
- 写操作自动备份

[Unreleased]: https://github.com/lfmmd/TrioAI/compare/v0.1.2...HEAD
[0.1.2]: https://github.com/lfmmd/TrioAI/releases/tag/v0.1.2
[0.1.1]: https://github.com/lfmmd/TrioAI/releases/tag/v0.1.1
[0.1]: https://github.com/lfmmd/TrioAI/releases/tag/v0.1
