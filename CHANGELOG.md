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
