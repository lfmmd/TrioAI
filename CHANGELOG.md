# Changelog

本项目所有重要变更都记录在此文件中。

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

## [0.1.28] — 2026-06-12

修复 lookup_command 去重参数比较 bug。

### 修复

- **`NormParam` 归一化参数比较** — JavaScriptSerializer 对 `full=true`（bool）返回 `"True"`，对缺省 key 返回 `null`，导致 `full=""` 和 `full="True"` 比较时误匹配。新增 `NormParam` 辅助方法：`bool` → `"true"/"false"`，`string` → 小写，缺失 → `""`，统一后精确比较。
- **跳过 `GetDictValue` 间接层** — 直接 `b.TryGetValue("input") as Dictionary` 获取历史 tool_use 的 input，避免 serialize/deserialize 路径丢失类型信息。

## [0.1.27] — 2026-06-12

API 请求 token 优化（减少重复数据传输 ~34%）。

### 优化

- **P0: lookup_command 运行时去重** — AI 在同一会话中反复调用 `lookup_command` 查询同一命令（如 MOVELINK 被查 9+ 次，每次返回 ~16KB HTML）。新增 `TryDedupLookupCommand` 在 `ExecuteTool` 入口拦截：扫描 `_conversationHistory` 已有相同 query+library+full 的成功调用时，返回 ~200 字节引用提示而非重新加载完整 HTML。预计节省 ~3.7MB 重复数据。
- **P1: Tool 定义静态缓存** — `GetToolDefinitions()` 每次调用重建 59 个 tool schema（~17KB）。改为首次构建后缓存为 `_cachedToolDefs`，后续返回浅拷贝。消除每次 API 请求的重复对象分配。
- **上下文压缩兼容** — `TryDedupLookupCommand` 扫描原始 `_conversationHistory`；`CompactHistory` 替换旧消息为摘要文本后扫描不命中，自然回退到正常执行，无副作用。

## [0.1.19] — 2026-06-11

界面 toolbar 新增消息数和 token 估算实时显示（Msgs: N ~XK tokens）。

## [0.1.18] — 2026-06-11

History 管理从硬预算截断改为 auto-compaction（参考 Claude Code）。

**变更**：
- History token 预算从 30K chars (~7.5K tokens) 提升至 500K chars (~125K tokens)，充分利用模型上下文窗口
- 最大消息保留数从 30 提升至 100
- 新增 auto-compaction：超出预算时调用 AI 摘要旧消息（而非直接丢弃），保留最近 30 条消息不变
- auto-compaction 摘要保留用户意图、代码变更、错误修复、工作状态等关键上下文
- 摘要失败时仍走原有截断逻辑作为兜底

## [0.1.17] — 2026-06-11

patch_source 操作格式从 `{action,line,content}` 重写为 `{old_string,new_string}` 文本替换模式。

## [0.1.16] — 2026-06-11

patch_source 重写为 old_string/new_string 文本替换模式（参考 Claude Code FileEditTool）。

**变更**：
- `patch_source` 操作格式从 `{action, line, content}` 改为 `{old_string, new_string}`——通过精确文本匹配定位替换位置，完全不依赖行号
- `old_string` 必须在源码中唯一匹配，不唯一时返回错误提示补充上下文
- 支持 Trim 后的模糊匹配，容忍行尾空白差异
- `old_string` 为空时在文件末尾追加 `new_string`
- `patch_source` 响应新增 `operations` 数组，返回每个操作的执行状态（replaced / skipped / appended）

**背景**：旧版基于行号的 patch 机制常因 AI 行号计算偏差导致修改错位。old_string/new_string 模式从根本上消除了行号偏移问题。

## [0.1.15] — 2026-06-10

程序类型感知（TrioBASIC / IEC ST / PLCopen 方言区分）。

**变更**：
- `GetProgramDialect(name)` 返回 `"triobasic"` / `"iec"` / `"unknown"`
- `read_source` 响应新增 `type` 和 `dialect` 字段
- `write_source` / `patch_source` 仅对 triobasic 程序执行 TrioBASIC 校验，IEC 程序跳过
- `LoadIndex` 加载全部三个库（triobasic + iec + plcopen）
- `lookup_command` 新增 `library` 参数，限定搜索范围
- `BuildProjectContext` 列出每个程序名和类型（取代聚合统计）
- System prompt 新增 `PROGRAM TYPE AWARENESS` 规则段

**修复**：
- IEC ST 程序不再被 TrioBASIC 验证器误拦截
- `PROGRAM MAIN` 不再被错误写入 IEC POU（AI 现在能看到程序类型）
- `lookup_command` 可按库限定，避免跨语言匹配

## [0.1.14] — 2026-06-10

lookup_command 三层机制 + 192KB 预算。

**变更**：
- `lookup_command(query)` 默认返回 Layer 2：name + signature + description（~500 字节/条）
- `lookup_command(query, full=true)` 返回 Layer 3：完整 HTML 文档（192KB 总上限，按比例截断）
- 签名动态从 index.json 的 desc 字段提取（triobasic ~25% 有签名，其余仅 name+desc）

后续计划：
- 国际化扩展（更多语言）
- 程序执行历史/审计日志
- HTTP API 鉴权（防止其他进程误调用）
- 多控制器切换支持
- IEC 断点的 line→CodeElement 反推（目前 SetBreakpoint 需在 MP UI 中手动设置）
- regex 硬拦 VB 模式（`Dim`/`Function...End Function`/`Class`/`Math.`/`Console.`）—— 目前规则准确率不足，暂未启用

## [0.1.13] — 2026-06-10

移除 Phase 1（全大写标识符白名单校验），仅保留 Phase 2（函数调用签名校验）。

**背景**：Phase 1 导致大量误杀（用户程序名如 `MOVE_DEMO:` 被当成幻觉命令拦截），AI 陷入死循环触发 `(Reached maximum iterations)`。误杀代价远大于漏杀（编译器兜底）。

**变更**：
- 移除 `_reAllCapsIdentifier`、`_reLineComment` 注释剥离 + 全大写扫描逻辑
- 移除 `IsAllUpperIdent()`、`GetProjectIdentifiers()` 等辅助方法
- 修正 `_reLineComment` 正则（`[^*\r\n]` → `[^\r\n]`），但随 Phase 1 一起移除
- Phase 2（`Name(args)` 函数调用签名校验 + 未知命令检测 + 参数数量检查 + 只读函数赋值检查）保持不变

**防御层级**：
- Layer 1: System prompt 规则
- Layer 2: Phase 2 函数调用校验（本次保留）
- Layer 3: TrioBASIC 编译器（兜底）

## [0.1.12] — 2026-06-10

`lookup_command` 重复查询去重版本（token 优化）。

### 优化

- **重复查询自动去重** — 长对话里 AI 经常多次 `lookup_command` 同一个命令（例如先查 `MOVE` 写代码，5 轮后又查 `MOVE` 检查语法）。每次返回的 HTML 文档约 16KB，重复 N 次 = N × 16KB 重复 token。本次在 `BuildTrimmedMessages`（请求组装阶段）加一层去重：
  - 遍历历史所有 `lookup_command` 的 `tool_use` 块，按 `query` 参数（大小写不敏感）分组
  - 每个 query 的**第一次**调用保留完整内容（HTML 命令文档）
  - 后续相同 query 的 `tool_result` 内容替换为 ~200 字节的引用占位符：
    ```
    [Duplicate of lookup_command("MOVE") — full content preserved at the first
     call earlier in this conversation. Reference that occurrence instead of
     asking again.]
    ```
  - `tool_use_id` 保持不变，配对不破坏，API 仍正常

### 收益

假设 30 轮对话里有 8 个不同命令各被查 2-3 次：
- 优化前：~20 次 × 16KB = 320KB
- 优化后：8 个唯一查询 × 16KB + 12 个重复 × 0.2KB ≈ 130KB
- **节省约 60%** lookup 相关 token

### 设计取舍

- **只对 `lookup_command` 去重** —— `read_source` 也会重复读，但用户改过源码后内容会变；`lookup_command` 是只读静态参考库，幂等，安全。
- **第一次必须保留完整内容** —— 不能把所有重复的都清空，至少要留 1 份完整的，AI 才能往回找到语法。
- **替换为引用而非清空** —— 引用文本明确告诉 AI「前面查过了，往回找」，避免 AI 困惑地重新查询。
- **大小写不敏感** —— `MOVE` / `move` / `Move` 视为同一查询。
- **只在请求组装阶段去重** —— UI/日志/chat_history 仍记录完整 tool_result，方便审计与回看。

## [0.1.11] — 2026-06-10

v0.1.10 验证器白名单污染修复版（32 项端到端测试 100% 通过）。

### 修复

- **IEC/PLCopen 库污染 TrioBASIC 白名单** — `EnsureValidationIndex` 和 `LoadIndex` 都遍历 `skills/*/` 全部子目录，结果 `skills/iec/AO-printf.html` 被加入 `_triobasicIds` → AI 写 `Printf()` 被判合法 TrioBASIC。改为只扫 `skills/triobasic/`，IEC 函数（printf / AO-printf 等）不再混入白名单。
- **`WAITS` / `DEFAULT` 关键字未列入白名单** — `WAITS`（等待同步，区别于 `WAIT UNTIL`）和 `DEFAULT`（`SELECT CASE DEFAULT` 分支）没有单独的 HTML 文件，也不在 `_builtinKeywords` 中，导致合法代码 `WAITS` 和 `SELECT CASE VR(0) CASE DEFAULT ... END SELECT` 被误拦。补到 `_builtinKeywords`。

### 验证

32 项端到端测试 100% 通过：
- **12 项合法 TrioBASIC**：FOR/NEXT、WHILE/WEND、BASE+MOVE、VR/TABLE 读写、IF/ELSEIF/ELSE、SELECT CASE DEFAULT、GOSUB/RETURN、WAITS、SIN/COS/ABS、RND、嵌套函数调用 — 全部不误拦
- **10 项 LLM 幻觉命令**：Sleep/Delay/Random/Foobar/Printf（含 lowercase/uppercase 变体）/WriteLine/Console.WriteLine/Math.Sqrt — 全部拦截
- **4 项参数错误**：ABS 多参数、SIN 多参数、MOVE/BASE 无参数 — 全部拦截
- **3 项赋值错误**：赋值给 SIN/ABS/RND — 全部拦截
- **3 项边界**：空字符串、纯注释、REM 注释 — 全部通过

## [0.1.10] — 2026-06-10

v0.1.9 验证器三处 bug 修复版。

### 修复

- **校验器读错字段名 → 整段校验失效** — v0.1.9 的 `write_source` 拦截读 `code` 字段，但 tool schema 实际是 `sourceCode`；`patch_source` 读 `new_line` / `new_content` / `line`，实际是 `content`。结果校验器一直拿到空字符串，永远不会拦截。本次改为读正确字段名。
- **SetArgCount 把可选参数算成必填** — TrioBASIC 文档的可选参数形式是 `axis0[, axis1[, axis2[, ...]]]`，旧实现按 `,` split 后逐个判断 `StartsWith("[")`，但 `[` 出现在参数名 *之后*，所以 `axis0[` `axis1[` `axis2[` 全被算成必填 → `BASE(0)` / `MOVE(100)` 被误拦（说成需要 ≥4 / ≥5 个参数）。改为：取第一个 `[` 之前的部分算必填，之后的全算可选。
- **VR / TABLE 等系统变量被误判为不可赋值** — Pattern 1 `value = NAME(...)` 命中后默认 `IsAssignable=false`，但 VR / TABLE 是双向的（可读可写），`VR(0) = 100` 是合法 TrioBASIC。改为：默认允许赋值，仅对显式列入 `_knownReadOnly` 的纯函数（`ABS` / `SIN` / `COS` / `SQRT` / `RND` / 字符串函数等 ~30 个）拦截赋值。
- **未知调用 `Name(args)` 不在索引时不拦截** — 旧 Phase 2 只对 `_signatures` 命中的函数做签名校验，对 `Foobar(1,2)` 这种幻觉命令直接 `continue` 跳过（误以为 Phase 1 已处理）。但 Phase 1 的全大写正则不匹配 `Foobar`，导致漏判。改为：`Name(args)` 形式若不在 `_triobasicIds`（含 VR/TABLE/ABS/MOVE... 全部命令），直接判为幻觉。

### 新增

- **HTTP 端点 `POST /api/validate_basic`** — 直接校验一段 TrioBASIC 代码，返回 `{ok, errors}`。用于：调试校验规则、CI 回归测试、不走 AI 的批量验证。body: `{"code": "..."}`，response: `{"ok": true/false, "errors": [...]}`。

### 验证

15 项端到端测试通过 14 项（最后一项 `Dim x As Integer` 是纯 VB 语法、无括号，需要 regex 黑名单才能拦，已记到优化方向）。

## [0.1.9] — 2026-06-10

TrioBASIC 写入前白名单 + 签名校验版本。

> ⚠️ **此版本有严重 bug，校验器读错字段名导致整段校验永不触发。** 请升级到 [0.1.10]。

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
