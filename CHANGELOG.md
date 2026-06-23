# Changelog

本项目所有重要变更都记录在此文件中。

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

## [0.3.35] — 2026-06-23

修复 `patch_source` 的 `old_string` 匹配过严：TrioBASIC 编译器大小写不敏感，但匹配层此前用 `StringComparison.Ordinal`（大小写敏感），导致 AI 记成全小写/全大写时 `old_string not found`，反复重试。顺带修掉 TrimEnd 模糊分支的偏移 bug。

### 修复

- **`patch_source` 匹配改为大小写不敏感（TrioBASIC 语义）** —— 此前 `FindText` 用 `IndexOf(Ordinal)` 严格比较，而 TrioBASIC 编译器对 `cVR_x` / `cvr_x` / `CVR_X` 一视同仁。AI 读完源码后凭语义记忆重写 `old_string`，常把混合大小写的标识符（如 `cVR_bakdecelper`）记成全小写（`cvr_bakdecelper`）—— 在 TrioBASIC 里这完全合法且等价，但被大小写敏感的匹配层判成「字符串不同」→ `old_string not found` → AI 反复重试（见 `chat_history/20260621_202830.json` msg#68-70 连续 3 次失败）。现 `FindText` 在精确匹配失败后追加一层 `OrdinalIgnoreCase` 回退，与目标语言语义对齐。`CountOccurrences`（唯一性检查）同步改用 `OrdinalIgnoreCase`，杜绝「查找命中但计数为 0」的语义割裂。
- **修 `FindText` 的 TrimEnd 分支偏移 bug** —— 旧实现 `source.IndexOf(srcLines[i], source.Length - source.Length)` 中 `length - length` 恒为 0，等价于「从文件开头找该行文本的首次出现位置」。当 `old_string` 是多行块、且块内某行文本在文件中重复出现时，返回的偏移指向另一处相同行 → 替换错位置。改为手动累加行长度计算真实偏移。
- **`FindText` 返回 `(pos, length)` 元组，替换用实际匹配长度切片** —— Trim 模糊分支下源码行可能带尾随/前导空白，实际匹配块比 `old_string` 长。旧实现用 `op.oldString.Length` 切片会切在匹配块中间，留下半截残留。现返回匹配块的真实长度，调用方据此切片。

### 行为影响

- AI 记错标识符大小写 → 此前必失败重试，现直接命中替换。
- 精确匹配仍优先（大小写正确时不走回退层，行为不变）。
- **唯一性保护未放宽**：重复出现的片段（如多个子程序里的 `VR(cvr_hmibtn0)=0` 清零模式）仍要求 AI 提供更多上下文才能定位，这是合理的安全拦截。

## [0.3.34] — 2026-06-21

补全 0.3.33 的方言切换：子 agent（research / review / debug / explore / verify）现在也感知当前项目方言，与主线对齐。

### 修复

- **子 agent 方言感知** —— 0.3.33 的方言提示词切换只覆盖主线 system prompt，子 agent 的 system prompt 是固定文本、且拿不到主线 `BuildDynamicContext`（项目上下文），导致 IEC 项目里 verify/review 不知用 `library="iec"` 查证、且缺 IEC 防漂移锚点。现给子 agent system 注入一段轻量方言上下文块：告知当前方言 + `lookup_command` 的 library 指向 + 关键防漂移锚点（TrioBASIC：防 VB/VB.NET 漂移；IEC：用 TC_* 非 PLCOpen、域 FB 逐引脚查证）。dialect 未知则不注入（保持旧行为）。该块不打 cache_control，与 memory 块同处理，不打断 prompt 块前缀缓存。

## [0.3.33] — 2026-06-21

系统提示词按编程方言（TrioBASIC / IEC ST）动态切换。IEC 项目首次获得与 TrioBASIC 同等深度的专属提示词，从源头抑制向 PLCOpen 漂移。

### 新增

- **方言专属系统提示词动态切换** —— 此前系统提示词以 TrioBASIC 为主、IEC 段仅简略数行，导致 IEC ST 项目漂移到 PLCOpen（`MC_MoveAbsolute` 等）。现内置两份自包含精炼提示词（`skills/prompts/triobasic.md`、`skills/prompts/iec.md`），运行时按「生效方言」选用。生效方言每轮解析：手动模式直接锁定；Auto 模式扫描项目程序推断主导方言（纯 IEC 项目用 IEC，混合 / 纯 TrioBASIC / 空项目默认 TrioBASIC）。
- **设置面板方言选择** —— 新增「提示词方言」下拉（Auto / TrioBASIC / IEC ST），可手动覆盖自动推断。配置持久化（`dialectMode`，默认 Auto）。
- **三层兜底读取** —— 提示词读取顺序：用户可编辑的 `DataDir/skills/prompts/{dialect}.md` → 插件打包源（DLL 同级 `skills/prompts/`）→ 内嵌通用 `DefaultPrompt`。确保新版本开箱即用（未点「初始化 Skills」也能读对），且部署异常不崩。

### 变更

- **提示词真源迁移** —— 单一 `DefaultPrompt`（C# 常量，偏 TrioBASIC）降级为通用兜底；方言真源改为打包资源 `skills/prompts/*.md`，随插件发布。
- **移除 `AI_INSTRUCTIONS.md` 落盘机制** —— 不再随部署用单一提示词覆盖写该文件（已废弃）。
- **缓存** —— 方言在会话内稳定，Block1 缓存大部分命中；仅手动切换方言那一轮 cache miss。

## [0.3.32] — 2026-06-18

用 Motion Perfect V5.7 自带的中英双语帮助文件替换三个参考库（TrioBASIC / IEC / PLCopen），并新增「使用中文帮助文档」开关（默认英文）。

### 变更

- **参考库切换为 V5.7 mkdocs 帮助** —— 三个库（triobasic / iec / plcopen）原数据来自旧版 .chm 解压，现替换为 MP V5.7 自带的 mkdocs 帮助（`Help/HTML/sites/{en,zh}/`）。V5.7 正文更干净（提取 `<article>` 后去掉约 90% 导航 chrome）、类别（type）更准（TrioBASIC 直接读 Type 标题，不再靠 .hhc 推断）。新增构建脚本 `build_skills_v57.py`。
- **中英双语 + 语言开关** —— 每个库现按 `{lib}/{lang}/`（en/zh）组织，自带中英两套。新增设置项「使用中文帮助文档」（`useChineseDocs`，默认关闭=英文）。`AiSkills.cs` 的 `LoadIndex` 按开关选语言目录，切换时清索引与详情缓存重载。
- **丢弃图片/导航资源** —— 纯文本 API 渲染不了图片，构建时丢弃每命令的图片目录与 mkdocs 的 `assets/`/`index.html`/`404.html`，体积更小。

### 修复（lookup 查询稳定性）

- **命令签名恢复** —— V5.7 mkdocs 的描述段落不含签名行（旧 .chm 把签名拼进 desc，约 25% 命令带签名）。`build_skills_v57.py` 新增 `extract_syntax`，从每页 Syntax 段 `<code>` 提取权威签名（如 `MOVE(distance1, ...)`、`value = ABS(expression)`），存入 index.json 独立 `sig` 字段；`SkillIndexEntry` 加 `Sig`，`ParseSignature` 优先用 `sig`（缺失回退 desc）。签名覆盖恢复到与旧数据持平（triobasic 约 25%），`lookup_command` summary 档与代码参数个数校验恢复正常。
- **库标识修正（回归）** —— `{lib}/{lang}` 目录改造时引入回归：`SkillIndexEntry` 缺库标识，多处 `Path.GetFileName(Dir)` 取到的是语言目录名而非库名。新增 `Lib` 字段，修正 `BuildSkillsCatalog` 分类、`lookup_command` 库过滤与返回值、`EnsureValidationIndex` 的 triobasic 判定与目录路径。
- **切换语言后重建校验索引** —— `SaveConfig` 切换中/英文时新增 `_validationIndexBuilt = false`，让 `_triobasicIds`/`_signatures` 按新语言目录的文件重建（zh 比 en 少约 4% 命令）。

## [0.3.31] — 2026-06-17

在 0.3.30 的 V5.7 适配之上，**恢复 V5.6 兼容**：同一个 `TrioAI.MPPlugin` 现在可同时运行在 Motion Perfect V5.6 与 V5.7 上。0.3.30 是过渡版（仅 V5.7），0.3.31 是首个双版本兼容版。

### 修复

- **V5.6/V5.7 双版本兼容（运行时反射）** —— 编译错误 API 在 V5.6→V5.7 被整体重做（成员互斥），静态绑定注定只能认一个版本。新增独立反射层 `CompileApiCompat`（AiCompat.cs），运行时探测实际 API 形态，让按 V5.7 编译的 DLL 同时在两版运行：
  - **编译事件**（`COMPILEStateEventArgs`）：V5.6 读 `ErrorCode/ErrorLine/ErrorDescription`、V5.7 读 `isError`+`Errors`，统一成同一错误列表，供 `OnCompileStateChanged`（AiService.cs）逐条展示与 `compile_state` 事件 payload（Handlers.cs）使用。
  - **CompileProgram**：反射调用，兼容 V5.6 返回单个 `TrioBasicError` 与 V5.7 返回 `List<TrioBasicError>`（字段名两版一致，按名反射读取），全部错误不丢。
  - **Parser_BAS.EnumTokens**：V5.6/V5.7 仅命名空间不同（`EnumTokens` 签名与 `EnumTokenDelegate` 两版一致），反射探测命名空间并把 lambda 重绑到运行时 `EnumTokenDelegate`，token 表校验两版均可用。

## [0.3.30] — 2026-06-17

适配 Motion Perfect V5.7（TrioSharedLibrary 5.7.2.0）的 API 变更；并加强主线"写后必须编译验证"流程。**此版本需 MP V5.7；V5.6 及更早不再兼容**（运行时会因 API 不匹配报 `MissingMethodException`）。

### 新增

- **主线 AFTER-WRITE COMPILE GATE（写后编译门禁）** —— 修复"主线改完代码不编译验证"的问题：主线 prompt 此前从未要求写后编译（那个名叫 `AFTER-WRITE SELF-CHECK` 的段落实际是写前检查）。现强制：写/改代码后立即 `compile_program`，报错则修+重编循环到零错误，clean compile 后再派 `verify` 二次确认，编译通过且 verify PASS 才报告完成。原 `AFTER-WRITE SELF-CHECK` 顺手改名 `PRE-WRITE SELF-CHECK`（消除"名叫 AFTER 却 DO THIS BEFORE"的歧义）。

### 修复（V5.7 兼容）

- **COMPILEStateEventArgs 错误模型重构** —— V5.7 移除 `ErrorCode/ErrorLine/ErrorDescription`，改为 `isError`(bool) + `Errors` 列表（每条 `ProgramBuildResultEntry{Line,Text,Code}`）。`OnCompileStateChanged`（AiService.cs）改用 `isError` 并逐条展示全部 `Errors`；`compile_state` 事件 payload（Handlers.cs）改为 `isError` + `errors[]`。
- **CompileProgram 返回类型变更** —— V5.7 的 `IController.CompileProgram` 返回 `List<TrioBasicError>`（空列表=无错），不再返回单个可空 `TrioBasicError?`。编译结果处理（Handlers.cs）改用 `Count>0` 判断 + 返回全部 `errors[]`（首条仍作 `error` 摘要）；移除 V5.7 已删的 `includeProgramName/includeProgramLine`。
- **Parser_BAS 命名空间迁移** —— V5.7 把 `Parser_BAS` 移到 `Trio.SharedLibrary.CodeCompletion.BAS`，AiControllerValidation.cs 补 `using`（`EnumTokenDelegate` 签名不变）。
- **开发引用 DLL 对齐 V5.7** —— 编译引用的 `..\Trio*.dll` 从 5.6.3.0 更新为 5.7.2.0（旧版备份为 `*.v56`）。

## [0.3.28] — 2026-06-17

子 agent（research/review/debug/explore/verify）跑完后，聊天流新增一条可折叠的内部轨迹消息，用户可见其每轮思考、工具调用与结果摘要。

### 新增

- **子 agent 内部轨迹折叠显示** —— 此前 subagent 跑时 UI 只有顶部进度条 banner，内部 thinking/工具调用/结果跑完即丢，用户无法看见它做了什么。现 `RunSubagent` 收集轨迹（每轮 `💭` 思考 + `🔧` 工具调用+入参 + `→` 结果摘要 + `── conclusion ──`），跑完通过新回调 `OnResearchTrace` 回传，ChatPanel 插一条深蓝色 `[agentType]` 折叠消息（复用现成 Expander）。每步摘要按 ~500 字截断，紧凑可读；取消/异常也回传已有部分。Expander header 按 Role 区分（普通消息="思考" / Subagent="📋 子 agent 轨迹"）。

## [0.3.27] — 2026-06-17

主线命令查证改走 research 子 agent（隔离上下文，full HTML 不进主对话）；research 内部分层 lookup；修 signature 空返回 bug；子 agent 跳过主线 dedup。

### 改进

- **主线 full 命令查证改走 research 子 agent** —— 此前主线直接 `lookup_command full=true` 把完整 HTML（~16KB/次）拉进主对话历史，是主上下文膨胀主因之一。现改为：需要命令完整语法/示例/参数时派 research 子 agent（在隔离上下文读 full 文档、返回精炼结论，raw HTML 不进主历史）；`lookup_command`（full=false）仅作轻量确认/签名。撤回 0.3.26 的"主线 lookup 强制 full=true"。
- **research 内部分层 lookup（两档）** —— 此前 research 对每个命令都 `full=true` 一次；改为先 `full=false`（name+signature+description），仅在不足以提取所需时才对该命令升级 `full=true`，多数命令只需 summary。
- **research 工具描述** 去掉"单命令别用 research"，改为单/多命令完整查证都可用 research（可批量）。

### 修复

- **修 lookup summary 档 signature 空返回 bug** —— `LookupCommand` 取签名表前未触发 `EnsureValidationIndex()`，导致 full=false 时 signature 经常空（日志 7 个命令全空）。补 `EnsureValidationIndex()` 后 summary 档带真实签名，分层第一档可用。
- **子 agent 跳过主线 lookup 去重（修误命中）** —— 主线去重扫 `_conversationHistory`，子 agent 调用在隔离 subMessages，既扫不到自己的、又会误命中主线历史返回 `"reference earlier tool_result"`（但子 agent 隔离看不到，被误导）。加 `_inSubagent` 标志，子 agent 执行期间 ExecuteTool 跳过去重。

## [0.3.26] — 2026-06-17

修复主线 system prompt 自身的 DIM 语法错误（曾误导 agent 把用户的裸 DIM 认证为"语法正确"），并强制主线 lookup 传 full=true。

### 修复

- **主线 safe-coding 对照表的 DIM 行原本是错的** —— 旧表把数组"正确"写法标为 `DIM arr(10)`（不带 AS type），与官方文档 `DIM name AS type(size)` 冲突。**这正是日志中 agent 对 FINDMARK 的 `DIM userregpos`（裸、不带类型）下"在 TrioBASIC 正确"结论的根因——system prompt 自己误导了它。** 现按 `skills/triobasic/DIM.html` 纠正：标量 `x = 0`（隐式 FLOAT）或 `DIM x AS FLOAT`；数组 `DIM name AS type(size)`；**新增一行：裸 `DIM x`（不带 AS type）在 TrioBASIC 非法**（VB.NET 合法 / TrioBASIC 非法，典型方言 drift）。
- **主线 lookup 强制 full=true** —— 旧版不带 full=true 只拿到截断摘要 + 空 signature，判不了带参数用法（此前仅 research 子 agent 强制）。MANDATORY 条款现明确要求 full=true，并说明不带 full=true 返回的是空 signature、不足以判断。
- **MANDATORY 点名声明/类型 drift 高危** —— DIM/AS/类型/数组是 BASIC 方言差异最大的地方，明令不得凭记忆判断这类语句，写或判断前必须 lookup。

> 此次错误来自主线模型，非子 agent；0.3.25 的子 agent 加强不覆盖主线，本次补主线侧。

## [0.3.25] — 2026-06-17

加强子 agent 对命令用法的查证要求：review/verify 对每个非常规/易错命令（运动/安全类）都查证（不再仅 when unsure）；debug 补查证涉及命令的用法。

### 改进

- **子 agent lookup_command 查证要求硬化** —— 此前 review/verify 的 lookup 要求是「when unsure」（不确定时才查），子 agent 若自以为确定（凭不准的训练记忆）就可能漏判错误命令用法；debug 的 discipline 甚至没要求查 lookup。改为：review/verify 对**每个非常规/易错命令调用**（尤其运动/安全类：MOVE/WAITS/CONNECT/WDOG/SERVO/BASE/AXIS/速度加速度参数等）都查证真实语法，明令「不靠记忆」，仅完全平凡的语句（简单赋值/基础运算）可跳过；debug 新增一条 discipline，对涉及代码依赖的命令（尤其运动/安全类）查证用法与前置条件（错误语法或缺失前置如 MOVE 前无 CONNECT/WDOG、缺 WAITS 是常见根因）。research/explore 的 lookup 要求不变（research 已要求每个相关命令 full=true 查一次，explore 仅 brief 命名）。

## [0.3.24] — 2026-06-17

子 agent 现在也注入持久化记忆，遵循用户偏好/项目约定（如中文注释）。

### 改进

- **子 agent 注入持久化记忆** —— 此前持久化记忆（memory.md）只注入主对话 system（`CallApiStream`），5 种子 agent（research/review/debug/explore/verify）的 system 只有各自定位 prompt，拿不到 memory——所以 review/debug 等子 agent 写结论时可能不遵循用户偏好（如注释语言）。现给子 agent system 追加 memory 块（`_memoryEnabled` 且 memory 非空时），与主线一致，让子 agent 遵循用户偏好/项目约定。子 agent 仍不拿主线历史/项目上下文/task 清单（独立上下文设计不变），memory 是它了解用户偏好的唯一补充来源。memory 块放在 prompt 块之后（prompt 块保留 cache_control 前缀缓存断点，memory 变化不影响其缓存）。

## [0.3.23] — 2026-06-17

修正 0.3.22 写工具免确认语义：编辑/编译类从「永久免确认」改为「本对话首次确认后放行」（每轮对话一道人工闸）；运行/上下传/变量写入类仍每次确认。

### 改进

- **编辑类免确认改为会话级首次许可** —— 0.3.22 把 6 个编辑/编译类工具（`write_source`/`patch_source`/`create_program`/`delete_program`/`rename_program`/`compile_program`）设为永久免确认（任何对话、每次都直接执行），但这意味着 AI 可在新对话一上来就随意改/删程序源码而用户完全不介入。改为**会话级首次许可**：当前对话内首次用到编辑/编译类工具时仍弹一次确认，用户点「允许」后本对话内这类操作自动放行、不再逐次确认；新对话或切换会话重新要首次许可。运行/上下传/变量写入类（`run_program`/`stop_program`/`set_program_process`/`upload`/`download`/`write_vr`/`write_table`/`write_iec_variables`）不受影响，仍每次确认。新增会话级状态 `_sessionEditApproved`（`AiSession.cs`），在 `StartNewSession`/`LoadSession`/`ClearHistory` 三处重置（AiHistory 历史压缩的 Clear 不重置，因为是同一对话内）。`AiOptimizationTests` 新增 P-S21（验证重置语义）。

## [0.3.22] — 2026-06-16

写工具按风险分级（程序编辑/编译免确认）；持久化记忆改为仅用户明确要求时 AI 才更新（不再自动塞）。

### 改进

- **写工具风险分级（免确认白名单）** —— 此前所有写工具（`WriteTools`，14 个）执行前都弹内嵌确认面板等用户点「允许」，频繁确认很繁琐。新增 `AutoAllowWriteTools` 子集（6 个低风险工具：`write_source`/`patch_source`/`create_program`/`delete_program`/`rename_program`/`compile_program`），确认闸门豁免它们——这些是纯工程文件操作（增删改源码 + 编译），可重建、不碰实时控制器，自动放行。其余 8 个（`run_program`/`stop_program`/`set_program_process`/`upload`/`download`/`write_vr`/`write_table`/`write_iec_variables`）影响控制器实时状态/行为，**仍需人工逐次确认**。`AutoAllowWriteTools` 是 `WriteTools` 的严格子集，Plan Mode 仍靠完整 `WriteTools` 拦截全部写工具，安全性不变。`AiOptimizationTests.cs` 新增 P-S20（验证子集关系 + 免确认成员 + 保留确认成员）。

- **持久化记忆改为用户主导（移除 AI 自动更新）** —— 此前 system prompt（`AiPrompt.cs` `BuildMemoryInstructions`）以 `MANDATORY` 强制 AI 在多种情况自动调 `update_memory` 写记忆，含「AI 自己发现重复问题及解决方案」「用户随口提及的项目细节（VR/轴/IO）」「用户纠正做法」等自主触发——导致 AI 每完成一个任务就往记忆塞内容。而记忆本应是用户主动记录（常用于记 AI 易犯的错），不该 AI 随便编辑。现改为**唯一触发=用户明确要求记住**（「记住…」「记住这个」「下次记住」「remember this」），并明令禁止 AI 自行更新：不得记录自主发现的问题/方案、用户随口提及的细节、自己的「教训」或纠正；拿不准是否该记时不写、等用户开口。`update_memory` 工具保留（用户要求时仍能用），记忆手动编辑（工具栏「记忆」按钮）照常。Rules 中「主动清理过时项（proactively）」改为「仅用户要求记忆新内容时」。UI 描述（`ChatPanel.cs` `MemoryDesc` 中/英）同步去掉「AI 会自动更新记忆」。

## [0.3.21] — 2026-06-16

新增「轻模型」：设置里多一个模型名输入框，填了则**查文档/探索类子 agent（research/explore）走它**，审查/诊断/验证类（review/debug/verify）仍走主模型保推理质量；留空则全部用主模型（= 现状）。主对话/写代码永远走主模型，不受影响。

### 新增

- **轻模型输入框 + 子 agent 按 agentType 分流** —— 此前所有 API 请求（主循环 + 5 种子 agent）共用单一 `_model`、都走主模型。现给底层唯一请求构造点 `CallApiOnce` 加 `model` 参数（调用方指定），配置加 `lightModel` 字段。模型路由按 agentType 分两层（与 thinking 分流同分组）：**research/explore**（查文档/探索，简单）走 `lightModel`（留空回退主模型）；**review/debug/verify**（审查/诊断/验证，需推理）固定走主模型不降级；主循环（`CallApiStream`）永远主模型。设置窗口（`ChatPanel.cs`）在「模型」下加「轻模型」输入框（`LoadConfigValue("lightModel")` 填值、保存时传入 `SaveConfig`）。**主对话/写代码/规划永远走主模型**，质量不受影响；只有 research/explore 这类后台查询/探索任务在填了轻模型名时改走轻模型，省 token/更快。轻模型须与主模型同 `apiUrl`/`apiKey` 下可用（如智谱 `glm-4-flash`、DeepSeek `deepseek-chat`）。留空 = 全主模型 = 现状，零行为变化。`AiOptimizationTests.cs` 新增 P-S19（三层验证：research→轻、research 空→回退主、verify→不降级主）；`CallApiOnce` 签名变更同步 7 处测试 mock。

## [0.3.20] — 2026-06-16

子 agent 进度 banner 文字不再显示固定轮数上限「/12」，只显示当前轮次。

### 改进

- **进度 banner 去掉固定轮数上限显示** —— 0.3.17 起 banner 文字格式为 `[review] 轮 2/12: read_source`，其中 `/12` 是 `SubagentMaxTurns` 固定上限。用户不希望 UI 暴露这个看起来硬编码的固定数字。改为 `[review] 轮 2: read_source`，只显示当前第几轮、去掉总数；进度条 `Maximum`/`Value` 逻辑不变（图形进度照常反映进度，满格仍对应 12 轮）。开始时的 banner 文案（`子 agent 正在审查…`）本就不含轮数，未动；`OnResearchTurn` 回调签名未变。

## [0.3.19] — 2026-06-16

主系统提示新增「子 agent 使用方法论」章节，教主模型正确使用 5 种子 agent（含如何按项目规模拆解大任务），弥补此前主 prompt 完全没有子 agent 使用引导、主模型常把「整个项目」整块塞给单个子 agent（~12 轮预算跑崩）的问题。

### 改进

- **主 prompt 加 USING SUBAGENTS 章节** —— 此前主系统提示（`DefaultPrompt` / `AI_INSTRUCTIONS.md`）里完全没有 research/review/debug/explore/verify 的使用引导，主模型只靠各工具自己的 description 决定怎么用，遇到「分析整个项目」「审查所有代码」这类项目级任务时，常直接把整块塞给一个子 agent——而子 agent 是 ~12 轮预算的短任务执行器，无法 scale 到项目规模，半路耗尽轮数返回残缺结论。新增 `## USING SUBAGENTS` 章节（插在 BATCH 章节后、SAFETY 前）：① 五种 agent 各自适用场景（explore 先摸结构 / research 查多文档 / review 审查 / debug 诊断 / verify 写完验证给 PASS/FAIL/PARTIAL）；② 核心约束——子 agent 是短任务、不能接整块项目；③ **大任务拆解 4 步（MANDATORY）**：先 explore/list_programs 摸规模 → 拆成单程序/单问题聚焦块 → 按类型分派 + task_create 跟踪 → 综合答复；④ verify 专属引导（非 PASS 判定必须处理或告知用户，不得忽略）；⑤ FORBIDDEN 整块派发。把「规模感知 + 拆解」职责明确写进主模型提示词，而非指望子 agent 自己判断。`BuildStablePrompt` 组装逻辑与 safe-coding 嵌入未改动。

  **生效**：改的是源码 `DefaultPrompt`，运行时读已部署的 `AI_INSTRUCTIONS.md`；后者仅 InitializeSkills 时强制覆盖（文件已存在不自动刷新）。故更新部署后须在 MP 里重新「初始化技能」一次，新引导才会写入并生效。

## [0.3.18] — 2026-06-16

对照 cc-haha 多 agent 框架审查 0.3.17 的多 agent 类型，补齐三处工程不足 + 新增 verify 验证子 agent。

### 新增

- **verify 验证子 agent（独立判定 PASS/FAIL/PARTIAL）** —— 主模型写完 / 改完程序后，派一个独立子 agent 做「第二意见」验证并给出明确判定，弥补 review 只列缺陷、缺「独立验证 + 判定」语义的空白。verify **保持只读**（与其余 4 种一致，不破坏只读隔离）：主模型写完本来就会 `compile_program`（编译错误是确定性的，编译器每次给同样答案），故把【源码 + 编译结果】一起传给 verify，verify 独立读源码 + 核对命令用法 + 交叉比对实时状态（轴是否连接、VR/TABLE 是否初始化、运行中进程是否设了期望值），输出 `VERDICT: PASS/FAIL/PARTIAL` + 依据；若传入的编译结果有错则直接 FAIL。verify 工具池 = 代码（`read_source`/`search_code`/`get_iec_task_detail`/`read_iec_variables`/`lookup_command`）+ 实时状态（`get_status`/`list_axes`/`read_vr`/`read_sysvar`/`read_table`/`list_processes`/`get_process_variable`/`get_events`）。`AiPrompt.cs` `GetSubagentPrompt` 加 `verify` 分支；`AiTools.cs` DispatchTool / BuildToolDefinitions / PureIoTools / SubagentToolPools 各加 verify；`ChatPanel.cs` banner 动词「验证」。`AiOptimizationTests.cs` 工具数 69 → 70。

### 改进

- **per-type 工具池（聚焦 + 省 token）** —— 0.3.17 四种子 agent 共用 35 个只读工具的 schema 暴露面，但每个 agent 的 prompt 只提到其中一部分，子 agent 可能调用 prompt 没告诉它的工具。新增 `SubagentToolPools`（`Dictionary<agentType, HashSet>`）：**research** = 35 全超集（万能查文档不削弱）、**review** = 10（读源码 / 搜索 / IEC / 命令用法核对）、**debug** = 13（实时状态优先 + 源码）、**explore** = 9（遍历程序 / 搜索 / 概览）、**verify** = 13（代码 + 实时状态）。`BuildSubagentToolDefinitions(agentType)` 按池过滤 schema；运行时拦截仍由 `SubagentReadTools` 超集兜底（双层防护不变）。

- **thinking 按 agentType（深度推理定向开启）** —— 0.3.17 子 agent 一律关 thinking 省 token，但 review / debug / verify 这类分析 / 诊断 / 验证任务深度推理有价值。改为：review / debug / verify 跟随全局 `_enableThinking`（budget = `_budgetTokens`），research / explore 始终关（查文档 / 遍历不需要）。

- **子 agent 失败语义（不再伪装结论）** —— 0.3.17 子 agent API 全失败或无文本产出时，返回 `[research subagent: API failed...]` / `[...completed with no textual conclusion]` 兜底字符串，主模型可能把它当真结论引用。`RunSubagent` 改返回 `(string conclusion, bool success)`：`success` = 是否产出文本结论；`DispatchTool` 在 `!success` 时返回 `{ error }` 触发 `tool_result.is_error`，主模型据此重试或如实告知用户。

`AiOptimizationTests.cs` 新增 P-S15（per-type 工具池：research=超集、各池为超集子集无写工具泄漏、特征工具裁剪边界）、P-S16（失败语义 success=false：API 全失败 / 无文本产出两路）、P-S17（thinking 按 agentType：全局开→review/debug/verify 开、research/explore 关）、P-S18（verify prompt 含 PASS/FAIL/PARTIAL 三态 + 明确只读不编译）；P-S4/7/8/14 适配 agentType 参数与元组返回。

## [0.3.17] — 2026-06-16

把单个 research 子 agent 扩展为**多 agent 类型**：新增 review（代码审查）、debug（问题诊断）、explore（广度搜索）三种子 agent，每种有独立的定位与结论格式，像 Claude Code 那样让主模型按任务性质选不同子 agent。

### 新增

- **review / debug / explore 三种子 agent** —— 0.3.16 的 research 子 agent（独立 messages 列表 + 只读白名单 + 只回传结论）只覆盖「查文档 / 读源码」一种调研。审查发现 review / debug / explore 本质**都是只读调研类**，现有 `SubagentReadTools`（35 个只读工具）完全覆盖三者的工具需求，差异**只在 system prompt 的定位与结论格式**。故 4 种共用 `RunSubagent` 全套机制（独立上下文 / 只读白名单 / 进度条 banner / 取消传播 / token 计入主线），只加 `agentType` 维度：`RunSubagent(task, agentType, maxTurns, ct)` → `GetSubagentPrompt(agentType)` 按 type 选 prompt。`AiPrompt.cs` 把 `BuildSubagentPrompt` 重构为 `GetSubagentPrompt`，4 个 prompt 各有定位——**research**（查命令文档 / 源码语法 → 精确 Syntax / Examples / Gotchas，照搬原 prompt）、**review**（代码审查员 → 读 `read_source` / `search_code`，核对 `lookup_command` 用法，输出 Critical / Warning / Style 三级 + `program:line` 的审查报告）、**debug**（诊断专家 → 优先查实时状态 `get_status` / `list_axes` / `read_vr` / `get_events` / `read_drive_param` 再结合源码，输出症状 / 根因 / 证据 / 修复方向的诊断报告）、**explore**（广度探索员 → `list_programs` → `read_source` 摘要 / `search_code`，输出程序索引 + 发现汇总，深挖单命令留给 research）。`AiTools.cs` DispatchTool 用 `case "research": case "review": case "debug": case "explore":` 共用一个分支（`name` 即 agentType）；新增 3 个工具 schema（结构同 research，description 各自写明定位 + 适用场景以防滥用）；`review` / `debug` / `explore` 加入 `PureIoTools`（同 research，避免主循环 `Task.Run` 内二次 `DispatcherHelper.Invoke`）。4 种共用只读白名单，无递归。`ChatPanel.cs` 进度回调 `OnResearchStart` / `OnResearchTurn` 加 `agentType` 参数，banner 文案按 type 显示动词（调研 / 审查 / 诊断 / 探索）。`AiOptimizationTests.cs` 新增 P-S10~14（4 个 prompt 非空且互不相同 + 未知 type 回落 research / 新 agent 已注册 / 属 PureIoTools / query 空校验 / review 运行时拦截 write_source），工具数断言 66 → 69。

## [0.3.16] — 2026-06-16

引入轻量 research 子 agent，让主模型把「查多个命令文档 / 大量源码」的研究重活委派到独立上下文执行，缓解主对话上下文被永久保留的参考内容撑大；同时修复 write_source / create_program 编辑器视图刷新静默失败。

### 新增

- **research 子 agent（上下文隔离）** —— 主循环是单一 `for` 循环、所有 tool_result 累积在 `_conversationHistory`，而 `lookup_command` / `read_skill` / `read_source` 的大块文档被 microCompact 永久保留（AI 写代码时反复引用的精确语法），是主上下文臃肿的根因。新增 `research` 工具：主模型把「需查多个命令文档 / 大量源码」的研究委派给一个在**独立 messages 列表**里跑的子 agent，复用 `ExecuteTool` 执行**只读工具白名单**（35 个：`get_status` / `list_*` / `read_*` / `lookup_command` / `read_skill` / `search_code` 等），跑完**只回传消化后的结论文本**，那些大块文档从不进 `_conversationHistory`。`AiService.cs` 抽出 `CallApiOnce`（参数化 messages / tools / thinking，子 agent 期间抑制 UI 流式回调），`CallApiStream` 改薄包装；新增 `AiSubagent.cs`（`RunSubagent` 主循环 + `CallSubagentWithRetry` + 纯逻辑 `ExtractText` / `BuildStubToolResults` / `ClampSubTurns` / `SubagentTrimIfNeeded`）、`AiPrompt.cs` `BuildSubagentPrompt`（精简研究版 system prompt，明令不写代码、结论 < ~2000 tokens、不重复查同一命令）、`AiTools.cs` `SubagentReadTools` 白名单 + `BuildSubagentToolDefinitions`（过滤 + 浅拷贝不污染主缓存 + 末项 `cache_control`）。双重保险：schema 不暴露写工具 + 运行时白名单拦截（`research` 本身也禁递归）；`research` 加入 `PureIoTools` 避免主循环 `Task.Run` 内二次 `DispatcherHelper.Invoke` 卡 UI。子 agent 关闭 thinking 以省 output token。**主循环零改动**——research 对它就是一个普通工具，结论作为 tool_result 进主线、由 microCompact 正常压缩（且不在永久保留白名单）。`AiOptimizationTests.cs` 新增 P-S1~9（ExtractText / stub 构造 / 白名单只读 + 禁递归 / 子 agent 工具集隔离不污染主缓存 / ClampSubTurns 钳制 / 取消立即传播 / write_source 运行时被拒 / 无 tool_use 单轮退出 / research 注册 + 纯 IO 分流 + query 空校验）。

### 修复

- **编辑器视图刷新静默失败** —— `Handlers.cs` `SetEditorTextSync` 原仅 pump 一次 `Loaded` 优先级的 dispatcher，文档刚打开或长时间未操作后视觉树未就绪时单次 pump 常拿不到控件 → 源码已写入项目但编辑器视图不刷新（用户以为没写进去）。改为最多 4 次 pump 重试并返回成功标志；`write_source` / `create_program` / IEC 源写入 3 处调用点据此：刷新失败时返回 `success: true` + `warning`（提示「源码已存入项目，重开程序查看」）而非静默返回 `success: true`。

## [0.3.14] — 2026-06-16

对照 cc-haha（claudecodefx）参考实现审查推理上下文处理，进一步修复 0.3.13 的 `KeepRecentThinking=1` 之后仍出现的「连续两次长 / 基本重复推理后陷入循环推理」问题。

### 修复

- **循环检测覆盖 thinking 指纹（治本）** —— 0.3.13 的失控循环检测只看 assistant **text** 指纹（前 60 字），对 **thinking 逐字重复**的循环完全失明：模型在思考里打转、但每轮 text 不同或纯 tool_use 时，检测永不触发，一路跑到 `MaxTurns=50`（GLM 不守 budget 时每轮 thinking 可达数万字）。根因在于 cc-haha 防循环的主力——`budget_tokens` 硬限制 + `thinkingClear` 服务端请求头——都依赖真 Anthropic 服务端，GLM 兼容端点拿不到。`AiService.cs` 抽出 `EvaluateLoopTurn` 纯逻辑方法，同时计算 text 指纹（前 60 字）与 thinking 指纹（前 120 字），各自独立累计连续相同次数，任一达到 `LoopDetectThreshold=3` 即提前终止（新增 `ThinkingFingerprintLen=120` 常量）。只抓「逐字重复」型；「持续接续打转、每轮开头不同但绕圈」型需相似度检测，留待后续。

- **尾部 thinking 过滤（对齐 cc-haha）** —— Anthropic 规范要求 assistant 消息不能以 thinking / redacted_thinking 块结尾（API 返回 400）。移植 cc-haha `filterTrailingThinkingFromLastAssistant`（`messages.ts:4897`）到 `AiHistory.cs` `BuildTrimmedMessages`：在 `EnsureValidMessageSequence` 之后，砍掉最后一条 assistant 末尾连续的 thinking 块，砍光则插占位 text。适配点：cc-haha 处理 messages 数组末尾恰好是 assistant 的情况，TrioAI 请求末尾恒为 user，故改为定位「最后一条 role==assistant」。正常 `[thinking,text]` / `[thinking,tool_use]` 的 thinking 在头部不受影响；仅清理「光想不说」的纯 `[thinking]` 或异常尾部 thinking，顺带减少无主 thinking 被回传。`AiOptimizationTests.cs` 新增 `Phase-Loop-1~4`（thinking 检测 / text 不回归 / 正常多步不误触发 / 纯 tool 轮不误判）+ `Phase-Filter-1~3`（纯 thinking 砍尾 / 头部 thinking 不动 / 末尾连续 thinking 砍除）。

## [0.3.13] — 2026-06-15

修复 write_source 对多行 TrioBASIC 程序的误报拦截，并解决 GLM 思考在长对话中越积越多、越来越长的循环思考问题。

### 修复

- **write_source 误报根因：禁用逐行 EXECUTE 验证** —— 三层 write_source 校验中最严格的 `ValidateByController`（`AiControllerValidation.cs`）用控制器 ValidationService 以**命令行模式逐行 EXECUTE 验证**，对多行 TrioBASIC 程序必然误报：runtime 命令（GOSUB/PRINT/RUN → #25）、变量赋值（#115）、多行结构（IF/WHILE/WEND/ELSEIF → #39/40/41）在真实程序里完全合法，逐行验证无法理解多行上下文。弊大于利（误报远多于真错），已禁用（`ShouldUseControllerValidation()` 固定返回 false）。真实代码错误仍由 `ValidateTrioBasicCode`（签名）+ `ValidateWithTokenTable`（token 表）两层白名单覆盖，二者都支持多行程序。若将来控制器提供「编译验证」API（非 EXECUTE 逐行）可在此重启用。

- **ELSEIF 被误判为未知函数调用** —— `AiValidation.cs` 函数调用签名校验用正则匹配 `Name(...)`，`ELSEIF (expr)` 这类控制流会被当成函数调用，而控制流关键字没有单独 HTML 条目、不在 `_triobasicIds` 白名单 → 误报「Identifiers not in TrioBASIC reference」。新增按 `_builtinKeywords`（含全部控制流 + 类型 + 运算符关键字）跳过。

- **思考累积 / 循环思考（对应 GLM clear_thinking）** —— GLM 官方说明：`clear_thinking=true`（默认）让每轮请求忽略历史轮次的 reasoning_content，若回传了历史 reasoning 则上下文越来越长、模型在旧推理上打转 →「思考越来越长停不下来」。此前 `AiHistory.cs` `BuildTrimmedMessages` 的 `KeepRecentThinking=3` 把**最近 3 轮** assistant 的 thinking 块原样回传进请求，正等于 `clear_thinking=false`。改为 `KeepRecentThinking=1`（只保留当前活跃那一轮的思考链头），等价 `clear_thinking=true` 效果。Anthropic/DeepSeek 同样接受删除历史 thinking（只禁止改 thinking content，删除整块合法），统一三家、不按端点区分（符合 thinking-unified 约定）。

## [0.3.12] — 2026-06-15

对照 cc-haha 参考实现审查对话 / plan / task 共有功能，修复主工具执行路径未标 `is_error` 的问题。

### 修复

- **工具失败标 `is_error`** —— Anthropic Messages API 约定 `tool_result` 用 `is_error: true` 标记工具执行失败，模型据此从结构上区分失败与成功并触发错误自愈。TrioAI 此前仅在历史裁剪恢复路径（`AiHistory.cs` 给缺失 tool_result 补合成 stub）用过 `is_error`，**主工具执行路径**（`AiService.cs` 组装 tool_result）的所有失败——异常 / Plan Mode 拒绝 / 用户拒绝 / TrioBASIC 验证拦截 / 未知工具 / 工具内部 error——都只把错误文本塞进 `content`，没标 `is_error`。新增 `AiTools.cs` `IsToolError(content)`（检测 `"Error: "` / `"BLOCKED:"` / `"User rejected"` / `{"error":...}` 顶层键），主循环据此标 `is_error: true`。`{"error":` 不误判 compile 的 `{"errors":[...]}`（复数 s 隔断）或 read_source 文本（JSON 转义后引号被转义）；compile 编译报错 `{success:false, errors:[...]}` 正确地不标（工具执行成功，只是结果含错）。`AiOptimizationTests.cs` 新增 `Phase-IsToolError` 覆盖 5 失败→true + 4 成功→false。

## [0.3.11] — 2026-06-15

基于 chat_history 日志实测定位并修复 GLM 思考过程失控（单块达 6.8 万字）与 AI 失忆循环（同一句重复 256 次）。

### 修复

- **thinking 客户端硬截断** —— GLM / 智谱 anthropic 兼容端点不执行 `thinking.budget_tokens` 截断（实测单块 thinking 达 6.8 万字，远超 budget=10000 的理论 ~5–7 千字）。`AiService.cs` `thinking_delta` 分支按 `budget_tokens×2` 字符（中文 ~2 字/token）封顶，超限丢弃后续 delta 并留截断标记。只控制本端显示/存储/回传，不减少模型实际 output token 消耗——要省 token 须关 `enableThinking` 或换支持 budget 的端点。

- **任务/Plan 状态每轮注入 system prompt** —— `SnapshotTasks()` 此前是 dead code（全项目零调用），AI 只能靠被 `TrimHistory` 压缩的 conversation history 里的 tool_use 链「记住」自己建了哪些任务，长对话失忆后重复建任务/重新规划。`AiPrompt.cs` `BuildDynamicContext()` 改为 instance 方法，每轮在 dynamic context 末尾注入当前任务清单（带「DO NOT recreate or re-plan」强约束）+ Plan Mode 状态。`SnapshotTasks` 返回类型 `List<object>` → 强类型 `List<Dictionary<string,object>>`（原匿名对象无法按字段读取）。

- **失控循环检测** —— agentic loop 新增内容层循环检测：连续 3 轮 assistant text 指纹相同（所有 text block 拼接、去空白、前 60 字）即判定失忆/空转循环，提前终止并发系统提示。`MaxTurns=50` 保留作硬兜底，此检测在内容层精准拦截，每轮 text 不同的合法多步推进（如批量处理多程序）不误伤。实测会话同一句重复 256 次跑满 50 轮、烧 49 万字 thinking，现提前在第 4 轮终止。

## [0.3.10] — 2026-06-14

修复「设置」「关于」对话框高度不足、底部按钮被截断的问题。

### 修复

- **设置窗口高度 + 滚动兜底** —— 0.3.8 新增 `localizeThinking` 等设置项后，`ChatPanel.cs` 设置窗口固定高度（520）装不下所有内容（7 个 checkbox + 4 个输入框，实测约 556px），底部按钮行（初始化 Skill 数据 / 取消 / 保存）被截断不可点。高度调至 600，并把内容 StackPanel 包进 ScrollViewer 兜底——未来再加设置项或高 DPI 缩放时按钮始终可达。

- **关于窗口高度** —— 高 DPI（125%/150%）下免责声明文本换行后空间不足，高度 300 → 380。

## [0.3.9] — 2026-06-14

两处 prompt / 缓存层面的优化，无新功能、无破坏性变更。

### 变更

- **思考语言指令强化** —— 0.3.8 的思考本地化指令是单独一句追加在长 prompt 末尾，偏弱、模型服从度低（实测装 0.3.8 后思考仍英文）。现合并进回复指令（一条 IMPORTANT 同时管 think+respond），措辞改为直接的 `Think in X` + 明确 `NOT in English`，并覆盖 `reasoning / thinking` 双术语（GLM 用 `reasoning_content`、Anthropic 用 thinking 块）。`AiPrompt.cs` 的 `GetThinkingInstruction` helper 随之移除，逻辑并入 `BuildStablePrompt`。

- **缓存命中优化：messages 的 cache_control 收敛到 1 个 breakpoint** —— 此前 `AiHistory.cs` `BuildTrimmedMessages` 给**每条 assistant 消息**都打 `cache_control`，长会话飙升到几十个 breakpoint（实测最多 49 个）。Anthropic 规范每请求上限 4 个，超限的多余 breakpoint 被服务端忽略 → 对应历史 prefix 未缓存 → 下次全量重传（实测 48% 请求 `cache_read=0`，未命中累计重传 435 万 token，比命中省的还多）。现改为只给【最后一条 assistant 消息】的末尾 block 打 1 个 breakpoint（system + tools 已占 2-3 个，总计落在 4 上限内），缓存整个历史 prefix，下次新增消息后该 prefix 仍稳定 → 命中。

## [0.3.8] — 2026-06-14

思考过程本地化：新增「思考过程本地化」开关，启用后 AI 的扩展思考（推理过程）跟随 MotionPerfect 系统语言，而非默认英文。

### 新增

- **思考过程本地化开关** —— 此前 `GetLanguageInstruction` 只约束**回复语言**，思考语言零约束 → 模型按固有默认用英文思考（回复中文、思考英文的割裂）。`AiPrompt.cs` `BuildStablePrompt` 现在开关开启时追加思考语言指令（如 `conduct your internal reasoning (your thinking blocks) in Chinese`），语言源同回复语言（`CurrentUICulture`）。开关默认**开启**（老用户升级 config 无此字段时走默认值自动启用）。设置面板新增 checkbox，4 语言文案（中/英/德/法）。这是 prompt 层引导，主流模型服从度较好但不保证 100%，故保留开关可关。

## [0.3.7] — 2026-06-14

对照 Anthropic 官方 extended thinking 文档，将 thinking 块处理统一为「照原样回传」，彻底移除 0.3.6 引入的 URL 硬编码区分。三家（Anthropic / GLM / DeepSeek）实为同一套约定。

### 变更

- **移除 `isRealAnthropic` URL 硬编码** —— 0.3.6 用 `_apiUrl` 含 `anthropic.com` 区分真 Anthropic / GLM 兼容端点。核对 Anthropic 文档后确认无需区分。
- **`EnsureValidMessageSequence` 移除「无 signature thinking 清理」逻辑** —— Anthropic 文档明确「If sending back thinking blocks, pass everything back **as you received it**」；400「thinking blocks cannot be modified」的根因是「重建消息」而非「无 signature」。此前按 signature 有无清理恰恰是文档点名的「重建消息」行为。真 Anthropic 完成块自带 signature、GLM/DeepSeek 完成块结构性无 signature，照原样回传三家都正确，也消除了 0.3.4/0.3.6 的「消息序列已被修复」刷屏。
- **`CallApiStream` 移除 `clear_thinking: false`** —— GLM 独有参数，真 Anthropic `thinking` 配置不认（会触发严格参数校验）。无法无条件加、又不能 URL 硬编码区分，故移除。多轮 thinking 上下文改由「照原样回传 thinking 块」提供（tool-use 多轮的规范要求，比 clear_thinking 更根本）。
- **流中断 partial thinking 不进历史** —— `AiService.cs` flush 路径（stream 中断收尾）不再把无 signature 的 partial thinking 块写入 `result.Content`。partial 块未收到 `content_block_stop`（signature_delta 只在 stop 前发），是唯一真正的毒块，从源头拦下即可，不靠事后按 signature 清理。

### 验证

- `AiOptimizationTests.cs` Phase-Thinking-2 翻转：无 signature thinking 块现在**应保留**（照原样回传），不再移除。

## [0.3.6] — 2026-06-14

对照 GLM 官方 thinking-mode 文档修正 thinking 处理（0.3.4 基于 Anthropic signature 假设，套到 GLM 上错误）。

### 修复

- **"消息序列已被修复"刷屏 + thinking 被删光** —— `AiHistory.cs` `EnsureValidMessageSequence` 的「移除无 signature thinking」逻辑改为条件化：仅真 Anthropic 端点（`_apiUrl` 含 `anthropic.com`）清理，GLM/兼容端点不清理。GLM 用 `reasoning_content` 字段、**无 signature 概念**（GLM 文档证实，67 会话 sig=0 印证），thinking 块本就无 signature；0.3.4 的无条件清理每次 API 请求都触发，删光所有 thinking 块 + 刷屏，且使 0.3.4 的多轮 thinking 升级在 GLM 上从未生效。

### 新增

- **GLM Preserved Thinking** —— `AiService.cs` `CallApiStream` 的 thinking 配置对 GLM/兼容端点加 `clear_thinking: false`（真 Anthropic 不加，它用 signature 机制且不认此参数）。对照 GLM 文档：「模型可在上下文中保留先前 assistant 回合的 reasoning content…`clear_thinking: false`」。启用后 GLM 保留前轮推理，多轮 thinking 真正连贯（此前即使回传 thinking 块，GLM 默认 clear_thinking 清除，每轮从头思考）。

## [0.3.5] — 2026-06-14

精简 AI tool 清单（66→64），修复命名不一致 bug，复活死代码 tool。

### 修复

- **`read_drive_params` 命名 bug** —— 定义给 AI 的名字是 `read_drive_params`（复数 s），但执行分发（DispatchTool）只认 `read_drive_param`（单数），AI 调用必返回 `Unknown tool`。统一为 `read_drive_param`（与 `write_drive_param`、case 一致）。
- **`rename_program` 死代码复活** —— 之前只在 WriteTools / DispatchTool case / Handler / HTTP API 出现，`BuildToolDefinitions` 缺定义，AI 永远看不到、永不调用。补上定义（参数 name + newName），Handler 已实现，现在 AI 可用。

### 变更

- **`get_sysvars` 从 AI 清单移除** —— 与 `read_sysvar` 功能重复（后者能读任意命名系统变量）。Handler 与 ApiServer HTTP 端点保留，外部调用方不受影响。
- **`open_oscilloscope` 从 AI 清单移除** —— AI 打开示波器窗口后无任何读波形/操作 tool 配套，价值有限。Handler 与 HTTP 端点保留。
- **`task_get` 移除** —— `task_list` 已返回全部字段（id/subject/description/status），`task_get` 无增量信息。一并删除 case、PureIoTools 条目、TaskGet 方法、相关测试。

## [0.3.4] — 2026-06-14

修复 extended thinking 多轮上下文断裂，以及自动压缩(compact)在 GLM-5.2+thinking 下从未成功、长期回退硬截断丢上下文的问题。

### 修复

- **thinking signature 全链路保留（多轮 thinking 不再断裂）** —— `AiService.cs` stream 解析新增 `signature_delta` 处理（覆盖式赋值，对照 Anthropic extended thinking 规范），构造 thinking 块时写入 `signature` 字段。之前完全丢弃 signature，导致传回 API 的无签名 thinking 被忽略，模型每轮从头思考（与历次"失忆/不连贯"问题同源）。`JavaScriptSerializer` 序列化 dict 保留所有 key，存盘/加载/trim 天然带 signature，无需额外改动。
- **自动压缩(compact)从未成功 → 已修复** —— `AiHistory.cs` `CallCompactApi` 重写为流式：① `stream:true`（主对话验证可用的路径，非流式从未验证）；② `_enableThinking` 时带 thinking 配置（防 GLM-5.2 兼容端点因配置不一致返回 400 或裸 thinking 块）；③ 流式只累积 `text_delta` 跳过 `thinking_delta`（防 thinking 当首块导致 text 取空）；④ HTTP 非 2xx 记录状态码+错误体到 `perf_error.txt`（之前静默 return null 无法排查）。修复前所有 compact 调用都失败回退硬截断，绕过工具去重/文件上下文恢复等全部优化。
- **小历史误触发压缩** —— `AiHistory.cs` `TrimHistory` 触发条件从「count>100 或 tokens>500K」改为「tokens≥500K 或 (count>100 且 tokens≥100K)」。新增常量 `CountTriggerTokenFloor=100000`。之前 14K tokens/101 条的小历史也触发压缩（继而因 compact 失败硬截断丢上下文）。
- **防御：移除无 signature 的 thinking 块** —— `AiHistory.cs` `EnsureValidMessageSequence` 末尾新增扫描，移除无 signature 的 thinking 块（来源：stream 中断 flush 的 partial thinking、升级前旧历史），防传回 API 触发 "thinking blocks cannot be modified" 400。每次 API 请求前都会清毒并永久写回历史。

### 新增

- **redacted_thinking 块支持** —— `AiService.cs` stream 解析新增 `redacted_thinking` 分支（`data` 在 `content_block_start` 一次性给出，无 delta 事件），Anthropic 安全过滤返回的该块不再被静默丢弃。

## [0.3.3] — 2026-06-14

完善批量多程序任务策略：明确 Plan Mode 不适用于批量同类任务，避免 AI 进 Plan Mode 后为"制定覆盖全部程序的计划"而一次性读取全部程序（context 洪泛）。

### 变更

- **批量任务不进 Plan Mode（prompt 硬规则）** —— `AiPrompt.cs` BATCH 段新增一条：批量同类任务（修复/检查全部程序）是 N 个**独立**子任务，不需要全局设计，**禁止 `enter_plan_mode`**（它强制 investigate everything first，会把全部程序读进 context，正是 0.3.2 BATCH 规则禁止的 context 洪泛）。批量任务直接 `task_create` 建清单 + 逐个执行；Plan Mode 留给真正需要全局设计的任务（重构新架构、多程序联动改造）。
- **`enter_plan_mode` 工具描述补充** —— `AiTools.cs` 明确标注避免用于"对多个独立项做同类操作"（fix/check all programs），这类是独立子任务，应走 task 系统 + 逐个处理。

## [0.3.2] — 2026-06-14

修复"加载旧会话后 AI 失忆自循环"——用户只发一次指令（如"同时创建3个demo"），AI 却把"开场白+创建动作"自动循环重复了 4 遍，4 组回答的 `thinkingText` 逐字完全相同（证明模型每次从同一上下文从头开始）。`chat_history/20260614_092608.json` 数据层验证：UI `messages` 里同一 user 指令重复 4 次，而发给 API 的 `history` 最终干净。根因双重：restore blob 把已完成动作当成待办、历史去重不覆盖连续相同 user 文本。

### 修复

- **AI 加载旧会话后自循环重做（连续重复 user 文本去重）** —— `AiHistory.cs` `EnsureValidMessageSequence` 新增"第二遍半"扫描（在 user tool_result 处理之后、assistant 补 tool_result 之前）：删除物理相邻且 `content is string`、内容逐字相同的 user 消息，保留第一条。自循环/旧脏数据/restore 累积产生的连续重复 user 文本，会被每轮 API 请求清理并永久写回 `_conversationHistory`（in-place 修复，与 0.2.16 同机制）。"相邻"判定 + `is string` 守卫确保不误伤用户真重发（中间隔 assistant 不触发）和 tool_result 消息（content 是 List）。新增 3 个 Phase2-* 单元测试。
- **restore blob 误导模型重做已完成动作** —— `AiSession.cs` `LoadSession` 拼装的会话恢复 blob 文案从模糊的"restored context"改为明确双语文案"[上一会话已完成的工作记录（仅作上下文，请勿重复执行）]"；工具调用记录 label 从 `System` 改为 `Tool`，让模型看清哪些动作已执行；配套注入的 assistant 回复也改为双语，明确"已记录、不会重做、按当前状态继续"。
- **重复加载会话时 UI 消息累积** —— `ChatPanel.cs` `LoadLastSession` 在 `_ai.LoadSession` 之后补 `_messages.Clear()`，与 `LoadSessionMessages` 行为一致，避免重复加载同一会话时 UI 消息重复堆积。
- **新对话未退出 Plan Mode** —— `AiSession.cs` `StartNewSession` 只清 history 不重置 `_planMode`，导致点"新对话"后上一会话的 Plan Mode 残留：write 工具仍被拒、UI 横幅不消失。现重置 `_planMode` 并触发 `OnPlanModeChanged(false)`（lock 外 invoke 避免 UI 回调重入）。

### 新增

- **批量/多程序任务逐个处理（prompt 硬规则）** —— `AiPrompt.cs` 新增 `BATCH / MULTI-PROGRAM TASKS` 段：对"修复/检查全部程序"这类多程序同类操作，AI 必须**逐个**处理（read → patch → compile 验证 → 下一个），禁止先把所有程序读进 context 再批量改——后者正是失忆/循环 bug 的 context 溢出触发点。配合 `task_*` 系统跟踪进度。
- **AI 工具支持十六进制地址** —— `AiJson.cs` 新增 `GetHexInt`/`GetHexLong`（解析 `0x4000`，无前缀先尝试十进制失败再按十六进制）；`AiTools.cs` 的 `read_vr`/`write_vr`/`read_table`/`write_table`/`read_drive_params`/`write_drive_params`/EtherCAT SDO 的 `index`/`subindex` 改用十六进制解析，与 `ApiServer.TryParseAddr` 一致，AI 可直接用 `0x` 地址操作 VR/TABLE/drive/SDO。
- **插件启动预热** —— `TrioAIPlugIn.cs` 加 WPF/JIT Prewarm 预热首次加载慢的程序集，缩短冷启动；清理上轮性能排查遗留的 `PerfLog`/`AssemblyLoad` 调试代码。

### 变更

- **Chat 串行保护** —— `AiService.cs` 新增 `_chatRunning` 防御层，兜底防止其他入口并发调用 `Chat`（正常由 `ChatPanel._isProcessing` 拦截）。
- **max_tokens 截断回落** —— `AiService.cs` 每条消息从默认 max_tokens 开始，仅当轮被截断时临时升级，避免升级后整个会话不回落。
- **工具取消时补 tool_result stub** —— `AiService.cs` 工具执行阶段被取消时，为已发起的 tool_use 补 stub tool_result + sentinel，避免下一条 user 与上一个 user/tool_result 块相邻（Anthropic 要求严格交替），与 API 流取消清理一致。
- **/api/status 版本号动态化** —— `ApiServer.cs` 从硬编码 `1.7` 改为读程序集版本。
- **breakpoint DELETE 参数校验** —— `ApiServer.cs` DELETE 断点要求 `line` 参数（1-based），缺失返回 error。

## [0.3.1] — 2026-06-14

Phase 1 测试发现的 4 个 bug 修复。重点解决"读 10 个代码做统计"时的严重失忆循环。

### 修复

- **multi-write 并行卡死（multi-write deadlock）** —— `ChatPanel.cs` `ShowInlineConfirmation` 用单实例字段 `_confirmTcs`，并行场景下多个 `Task.Run` 同时 `BeginInvoke` 进 dispatcher 队列，后者覆盖前者，用户点 Allow 只解锁最后一个 tcs，前几个 tcs 永远完不成 → `Task.WaitAll` 死锁。新增 `_confirmLock` 字段，`ShowInlineConfirmation` 整体 `lock` 包裹强制串行。
- **Plan Mode 审批弹窗 → 气泡化** —— `ChatPanel.cs` `ShowPlanApproval` 删除 `MessageBox.Show`，复用现有 `_confirmPanel` + `_confirmTcs` + `OnConfirmAllow`/`OnConfirmReject` 机制。审批按钮嵌入聊天界面底部，与 write 确认同一面板风格。同时 `lock (_confirmLock)` 防止与 write 确认冲突。
- **`_recentReadFiles` 容量过小导致 CompactHistory 后失忆** —— `AiSession.cs` `MaxRestoredFiles` 5 → 20，`MaxRestoredFileChars` 4000 → 6000。原来读 10 个代码做统计时，LRU 只保留最后 5 个，前 5 个被淘汰，触发 CompactHistory 后无法恢复。
- **microCompact 清空 read_source 内容导致 90 次重读循环** —— `AiHistory.cs` microCompact 逻辑保留最后 N=5 个 tool_result 完整内容，例外清单只有 `lookup_command` / `read_skill`，**`read_source` 不在例外**。读 10 个不同文件时前 5 个 tool_result 被清空成 `"[Old tool result content cleared]"`，AI 看到清空提示后重新读，形成恶性循环。把 `read_source` 加入 `keepRecent` 例外（read_source 已有同文件去重，不会无限累积）。

## [0.3.0] — 2026-06-14

Phase 1 优化：补齐 agent 自主编程的脚手架（对照 cc-haha 缺失项），不动现有架构，不引入新依赖。共 9 项后端改动 + Plan Mode UI + 7 个新增单元测试。

### 新增

- **工具并行执行** —— `AiTools.cs` 新增 `PureIoTools` HashSet（lookup_command / read_skill / discover_skills / task_* / enter_plan_mode / exit_plan_mode 共 9 个），这些工具绕过 `DispatcherHelper.Invoke` 直接执行；`AiService.cs` agentic loop 把 `foreach` 串行执行改为 `Task.Run` + `Task.WaitAll` + 按原始 tool_use 顺序收集 tool_result。真并行仅对纯 IO 工具生效（其他工具仍走 UI 线程序列化，MP API 是 STA）。
- **API 错误分类与重试** —— 新增 `RetryableApiException`（携带 StatusCode + RetryAfterSeconds），`CallApiStream` 对 5xx / 429 / IOException / HttpRequestException 抛此异常；新增 `CallApiWithRetry` 做指数退避重试（1s/2s/4s，共 3 次）。Retry-After header 解析。4xx 其他错误不重试。Chat loop 调用点改为 `CallApiWithRetry`。
- **Task/Todo 系统** —— 新增 4 个工具 `task_create` / `task_update` / `task_list` / `task_get`。`AiSession.cs` 新增 `_tasks` List + `_tasksLock`。AI 可自跟踪多步任务进度，任务状态不进入 conversation history（不污染上下文）。
- **Plan Mode** —— 新增 `enter_plan_mode` / `exit_plan_mode` 工具 + `_planMode` 状态字段。Plan Mode 下所有 `WriteTools` 工具调用直接拒绝（返回 BLOCKED 提示）。`exit_plan_mode` 通过 `OnConfirmPlan` 回调请求用户审批；UI 未挂回调时默认批准避免 AI 死锁。
- **discover_skills 工具** —— 列出所有 markdown skill 的 name/description/when_to_use/category，AI 在工具选择阶段就能感知可用技能，比 read_skill 试错更高效。
- **Plan Mode UI** —— `ChatPanel.cs` 新增橙色状态条（Plan Mode 激活时显示在 toolbar 下方）+ Plan 审批 MessageBox 对话框（显示 AI 提交的 plan 文本，Yes/No）。挂 `OnPlanModeChanged` + `OnConfirmPlan` 回调。
- **单元测试** —— `AiOptimizationTests.cs` 新增 7 个 Phase1-* 测试：PureIoTools 分流、新增 7 工具注册、MaxTurns/TokenBudget 常量、RetryableApiException + 指数退避、Task CRUD、Plan Mode 拦截 + 自动批准、EnsureValidMessageSequence 清理孤立 tool_result。`P1-1` 工具数断言从 59 更新到 66。

### 变更

- **循环退出条件** —— `AiService.cs` `MaxTurns` 从硬编码 20 改为常量 50；新增 `TokenBudgetLimit = 400_000` chars（≈100K tokens），循环内每轮检查，超阈值先尝试 `TrimHistory`，仍超则提示用户开新会话。
- **read_source 工具描述** —— 强化分页引导语气，明确告诉 AI "first chunk 不是完整文件，必须用 startLine/endLine 继续读"。
- **CompactHistory 失败提示** —— 摘要失败回退到硬截断时，UI 显示 `⚠ 自动摘要失败，已回退到硬截断（最近 30 条消息已保留）`，不再静默丢上下文。

### 修复

- **TrimHistory 硬截断兜底** —— 之前硬截断路径只重置 `_conversationHistory`，没调 `EnsureValidMessageSequence`，可能留下孤立 tool_result 导致后续每次 API 请求触发修复刷屏。现在硬截断后立刻调用一次清理。

## [0.2.16] — 2026-06-13

### 修复

- **根治"消息序列已被修复"反复刷屏（持久化 in-place 修复）** —— 0.2.15 只在 `CompactHistory`/`TrimHistory` 切点净化防止产生新孤立 tool_result，但 `EnsureValidMessageSequence` 之前只在请求副本 `messages` 上修，**不写回 `_conversationHistory`**。一旦历史里已有坏数据（旧版本遗留 / LoadSession 加载的老 session 文件 / 取消时塞的 sentinel 等），每次 API 请求都重复"复制坏历史 → 修副本 → 提示 → 副本发送 → 下次又是坏原数据"循环。现在 `BuildTrimmedMessages` 入口对 `_conversationHistory` 本身做 in-place 修复：第一次触发时修好历史并提示一次，后续请求 `repaired=false` 不再提示。末尾对 messages 副本的修复保留作兜底。

## [0.2.15] — 2026-06-13

### 修复

- **`CompactHistory`/`TrimHistory` 切点净化，根治"消息序列已被修复"频繁刷屏** —— `AiHistory.cs` 之前 `compactEnd = Count - MaxRecentKeep` 按数量硬切，若切点恰好落在 `assistant(tool_use_X) → user(tool_result_X)` 之间，`assistant(tool_use_X)` 被压进摘要、`user(tool_result_X)` 保留下来成为孤立 tool_result。`EnsureValidMessageSequence` 不写回 `_conversationHistory`，导致后续每次 API 请求都重复修复并刷屏"⚠ 历史修复: 消息序列已被修复"。新增 helper `IsUserToolResultMessage`：CompactHistory 切点处若是孤立 `user(tool_result)` 就 `compactEnd++` 把它一起压进摘要；TrimHistory 扫描时跳过 `user(tool_result)`，让切点不落在孤立配对上。从源头不再产生孤立 tool_result。

## [0.2.14] — 2026-06-13

### 技能加载机制（参考 claudecodefx cc-haha 设计）

- **Markdown skill frontmatter 新增 `when_to_use` 字段** —— `AiSkills.cs` 的 `ParseSkillMd` 解析 `when_to_use`/`whentouse`/`when-to-use` 三种写法，`MdSkillEntry` 增加 `WhenToUse` 字段。让 AI 在 Available Skills 列表里就能看到"何时使用该 skill"的指引，不必先 read_skill 才知道用途。
- **Available Skills 列表 token 预算 + 截断** —— 新增 `FormatSkillListing`/`FormatSkillEntry`：整个列表上限 8000 字符（约 1% 上下文），单条上限 250 字符，超预算时按均分截断到名字-only。为未来 skill 数量增长做防护。safe-coding 全文嵌入行为不变（安全保护，永不降级）。
- **`read_skill` 工具描述加 BLOCKING REQUIREMENT** —— `AiTools.cs` 的 read_skill 描述新增"当用户请求匹配 skill 的 'Use when:' 描述时，必须先调 read_skill 再写代码"的硬约束，并说明已嵌入 system prompt 的 skill（如 Safe Coding Rules）不需要重读。修复之前 AI"看到 skill 但不主动调用"的问题。
- **`read_skill` 结果跨 microCompact 保留** —— `AiHistory.cs` 的 `BuildTrimmedMessages` 把 `read_skill` 纳入 `keepRecent` 白名单（与 `lookup_command` 同等处理），microCompact 不再清空已读 skill 的正文。

### 提示词

- **Reference Libraries 节新增类别清单** —— `BuildSkillsCatalog` 把每个库（triobasic/iec/plcopen）的 `type` 字段归一化后取 top 8 类别（如"Axis Parameter (221), System Command (86), ..."），让 AI 知道每个库里有哪些类别存在，从"任务→类别名"反推更精准的 `lookup_command` 查询词。运行时从 index.json 汇总，自动随库内容更新。新增 `NormalizeType`（去末尾句号/去括号注释/压空白）和 `SummarizeTypes`（按条目数降序 top 8）两个辅助方法。+~95 token 成本。

## [0.2.13] — 2026-06-13

### 国际化

- **`AiHistory.cs` 6 处系统提示多语言化** — 补齐 0.2.12 多语言化遗漏的部分：`⚠ TrimHistory triggered`（裁剪触发统计）、`Auto-compacting conversation history...`（压缩开始）、`Compacted into summary`（压缩完成），以及 3 处 `⚠ History repair: ...` 历史修复提示（跳过非 user 消息 / 无 user 消息插入占位 / 消息序列已被修复）。复用 0.2.12 引入的 `Lang.L(zh, en)` 帮助函数。

## [0.2.12] — 2026-06-13

### 国际化

- **新增 `Lang.L(zh, en)` 帮助函数** — `ChatPanel.cs` 复用现有 `LangCode` 检测，返回当前 UI 语言对应的字符串。一次性系统消息只需 zh/en 两种翻译；de/fr/es/it/pt-BR/hu/ro/ru/sv 等其他语言 fallback 到 en。
- **`AiService.cs` 11 处用户可见字符串多语言化** — 包括 `(Reached maximum iterations)`、`[Compile Error]`、`Backup saved`、`API key not configured`、`Network error`、`Failed to call AI API`、`max_tokens` 升级提示（双向修复：之前 0.2.8 之后部分消息只写了中文，英文模式下也显示中文）、`API Error`（3 处）、`已备份`、`ERROR`。
- **修复 `(Reached maximum iterations)` 在中文 MP 中显示英文的问题**。

## [0.2.11] — 2026-06-13

### 文档

- **修正 `API.md` 的 `patch_source` 文档** —— 之前还是老的 `{action,line,content}` 格式(CHANGELOG 0.1.40 改格式时漏更新),实际代码早已是 `{old_string,new_string}` 文本替换模式。同步补上 `pouName` 参数说明、"程序必须已存在"前置条件、`old_string` 唯一性与空字符串追加语义。

## [0.2.10] — 2026-06-13

### 提示词

- **显式化 `patch_source` 前置条件** —— `AiPrompt.cs` 的 "WRITING LARGE PROGRAMS" 段落增加硬规则:`patch_source` 仅适用于已存在的程序,新建程序必须用 `write_source`(必要时先 `create_program`)。`AiTools.cs` 的工具描述也明确"REQUIRES the program to already exist — cannot create new programs"。修复 AI 在文件不存在时仍优先尝试 patch_source 导致失败的问题。

### 诊断

- **移除 0.2.8 加的 `LoadSession` 诊断日志** — 失忆 bug 已定位为老 session 文件缺 `history` 字段（0.2.x 之前保存），用户选择直接删除老 session，无需为此修改 LoadSession 兜底逻辑。

## [0.2.8] — 2026-06-13

### 诊断

- **`LoadSession` 加详细诊断日志** — 定位 `_ai.LoadSession` 加载 history 失败导致 AI 失忆的根因。日志直接写入 `%APPDATA%/TrioAI/perf_error.txt`,记录文件读取、反序列化、history 加载、摘要注入每一步状态,以及异常类型 + message + stack。验证完成后将清理。

## [0.1.39] — 2026-06-12

`EnsureValidMessageSequence` 增加跨消息 tool_use ID 去重。

### 修复

- **跨消息 tool_use ID 去重** — 两条 assistant 消息中出现相同 id 的 tool_use 时，丢弃重复的。API 要求 tool_use id 全局唯一，重复会导致请求被拒绝。
- **孤立 tool_result 检查优化** — 改用预收集的 `validToolUseIds` 集合判断，不再逐条遍历前面的消息，性能更好。

## [0.1.38] — 2026-06-12

重写消息序列防御逻辑，参考 claudecodefx 的 ensureToolResultPairing。

### 修复

- **新增 `EnsureValidMessageSequence`** — 四遍扫描修复 messages 数组：
  1. 移除孤立 tool_result（没有对应 assistant tool_use 的）
  2. 为缺失 tool_result 的 tool_use 插入合成错误结果
  3. 确保首条是 user 消息（跳过前导 assistant）
  4. 空数组兜底：插入占位 user 消息而非清空
- **修复 `messages:[]` 导致 1214 错误** — 旧逻辑在所有消息都是孤立 tool_result 时会 `messages.Clear()`，空数组也是非法的。现在改为插入占位文本。

## [0.1.37] — 2026-06-12

修复 1214 API 错误：孤立 tool_result 作为 messages 首条导致参数非法。

### 修复

- **`BuildTrimmedMessages` 防御增强** — 原防御只处理首条是 `assistant` 的情况。当 `TrimHistory` 截断把 `assistant` 和前面的 `user` 都删掉后，会留下孤立的 `tool_result` 作为首条消息，API 要求 `tool_result` 必须紧跟 `assistant` 的 `tool_use`，因此返回 1214 错误。现在检测并跳过孤立的 `tool_result`，直到找到合法的首条消息。

## [0.1.36] — 2026-06-12

修复 `write_source` 控制器验证器对 DIM 变量名的误报。

### 修复

- **`ValidateByController` 跳过 DIM 变量误报** — 控制器 `ValidationService` 逐行验证时不理解 DIM 上下文，将用户声明的变量名（如 `conv_speed`、`cycle_no`）误报为非法命令，导致 `write_source` 被拦截。现在验证前预扫描 DIM/LOCAL/GLOBAL 声明的变量名，遇到包含这些变量名的错误时跳过。
- **新增 `ScanDimVariables()`** — 解析 DIM 语句提取变量名（支持数组下标和 AS 子句）。
- **新增 `IsDimVarError()`** — 匹配错误信息中的 DIM 变量名。

## [0.1.35] — 2026-06-12

添加系统提示词：patch 失败时禁止整文件重写。

### 新增

- **系统提示词新增 "NEVER REWRITE ENTIRE FILES" 规则** — 当 `patch_source` 失败时，强制 AI 重新读取源码、分析不匹配原因、重试 patch，最多重试 3 次后询问用户。明确禁止回退到 `write_source` 重写整个已有程序。

## [0.1.34] — 2026-06-12

lookup_command 去重增加 library 维度区分。

### 修复

- **`BuildTrimmedMessages` 去重 key 加入 library** — 原去重 key 为 `query+full`，不同 library 的同名命令被误判为重复。现在 key 为 `query+full+library`，按库独立去重。

## [0.1.33] — 2026-06-12

修复 lookup_command 去重逻辑：brief 查询不应阻止后续 full 查询。

### 修复

- **`BuildTrimmedMessages` 去重改为 query+full 组合** — 原来只按 query 去重，导致先查 brief 再查 full 时完整 HTML 被替换为占位符。现在 full=true 和 brief 作为不同 key 分别去重。
- **`TryDedupLookupCommand` full→brief 方向去重** — full=true 的结果可替代 brief 查询（反向去重），但 brief 不能阻止后续 full=true 查询。

## [0.1.32] — 2026-06-12

修复 `GetStr` 对 bool 值的大小写归一化，根治去重与工具执行行为不一致。

### 修复

- **`GetStr` bool 归一化** — `JavaScriptSerializer` 将 JSON `true` 反序列化为 C# `bool true`，`.ToString()` 产生 `"True"`（大写T）。工具执行检查 `== "true"` 失败，返回轻量结果；去重用相同 `GetStr` 也得到 `"True"`，两个 `"True"` 恰好匹配，导致不同 `full` 参数的调用被错误去重。现在 `GetStr` 对 bool 值统一返回小写 `"true"` / `"false"`，工具和去重行为完全一致。

## [0.1.31] — 2026-06-12

修复去重参数比较与工具执行行为不一致。

### 修复

- **去重改用 `GetStr` 代替 `NormParam`** — `NormParam` 将 bool `true` 归一化为 `"true"`（小写），但工具执行用 `GetStr` → `.ToString()` 得到 `"True"`（大写T），导致 `full=true`(bool) 被工具视为非 full 但被去重视为 full，产生错误去重。现在去重和工具使用完全相同的值提取方式。
- **`full` 精确匹配** — `"True" != "true"` 不匹配（反映工具的实际判断），`"true" == "true"` 匹配。
- **`library` null vs 值不匹配** — `null != "iec"` 不匹配，避免跨 library 错误去重。

## [0.1.30] — 2026-06-12

添加去重诊断日志。

## [0.1.29] — 2026-06-12

修复 messages 参数非法导致 API 1214 错误。

### 修复

- **`BuildTrimmedMessages` 防御检查** — 历史被异常截断后 messages 数组可能以 `assistant` 角色开头，违反 Anthropic Messages API 要求。新增跳过前导 assistant 消息的逻辑，确保首条始终是 `user`。
- **`TrimHistory` 截断兜底增强** — 原逻辑只匹配 `user + string content` 消息，在 agentic 循环中全部 user 消息都是 `tool_result`（list content），导致找不到截断点。改为兜底匹配任意 user 消息，避免意外保留 assistant 开头的历史。
- **`TrimHistory` 诊断日志** — 触发裁剪时输出消息数和 token 估算，帮助追踪异常触发条件。

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
