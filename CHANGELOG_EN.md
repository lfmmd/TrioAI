# Changelog (English)

All notable changes to this project are documented in this file.
The Chinese version is at [CHANGELOG.md](CHANGELOG.md).

Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.3.12] — 2026-06-15

Audit of conversation/plan/task shared features against the cc-haha reference; fixed the main tool-execution path not marking `is_error`.

### Fixed

- **Mark tool failures with `is_error`** — The Anthropic Messages API marks a failed `tool_result` with `is_error: true` so the model can structurally distinguish failure from success and trigger error self-repair. TrioAI previously used `is_error` only in the history-trim recovery path (`AiHistory.cs` synthesizing a stub for a missing tool_result); the **main tool-execution path** (`AiService.cs` assembling tool_result) put every failure — exception / Plan Mode rejection / user rejection / TrioBASIC validation block / unknown tool / tool-internal error — into `content` without marking `is_error`. New `AiTools.cs` `IsToolError(content)` (detects `"Error: "` / `"BLOCKED:"` / `"User rejected"` / top-level `{"error":...}` key); the main loop marks `is_error: true` accordingly. `{"error":` does not false-match compile's `{"errors":[...]}` (plural `s` breaks it) or read_source text (JSON-escaped quotes); compile errors `{success:false, errors:[...]}` are correctly not marked (tool executed successfully, result just contains errors). New `Phase-IsToolError` test in `AiOptimizationTests.cs` covers 5 failures→true + 4 successes→false.

## [0.3.11] — 2026-06-15

Located and fixed GLM thinking runaway (single block reached 68K chars) and AI amnesia loop (same sentence repeated 256×) — both found by analyzing chat_history logs.

### Fixed

- **Client-side thinking hard-cap** — The GLM / Zhipu Anthropic-compatible endpoint ignores `thinking.budget_tokens` truncation (measured: a single thinking block hit 68K chars, far above budget=10000's theoretical ~5–7K). The `thinking_delta` branch in `AiService.cs` now caps at `budget_tokens×2` characters (~2 chars/token for Chinese), dropping further deltas and leaving a truncation marker. This only controls local display / storage / round-trip — it does not reduce the model's actual output tokens; to save tokens, disable `enableThinking` or switch to a budget-honoring endpoint.

- **Task / Plan state injected into the system prompt every turn** — `SnapshotTasks()` was dead code (zero call sites project-wide). The AI could only "remember" its tasks via the tool_use chain in `conversation_history`, which `TrimHistory` compresses — so in long chats it forgot and re-created tasks / re-planned. `BuildDynamicContext()` in `AiPrompt.cs` is now an instance method that appends the current task list (with a "DO NOT recreate or re-plan" constraint) + Plan Mode status to the dynamic context every turn. `SnapshotTasks` return type `List<object>` → strongly-typed `List<Dictionary<string,object>>` (the old anonymous objects couldn't be read by key).

- **Runaway-loop detection** — The agentic loop now does content-level loop detection: 3 consecutive turns with the same assistant text fingerprint (all text blocks concatenated, trimmed, first 60 chars) is judged a stuck loop and aborted early with a system message. `MaxTurns=50` stays as the hard backstop; this detects at the content layer and does not false-trigger on legitimate multi-step progress (where each turn's text differs). A measured session repeated the same sentence 256×, ran the full 50 turns, and burned 490K chars of thinking — now aborted at turn 4.

## [0.3.10] — 2026-06-14

Fixed insufficient height of the Settings / About dialogs, where the bottom button row was clipped.

### Fixed

- **Settings window height + scroll fallback** — After 0.3.8 added settings like `localizeThinking`, the fixed height of the Settings window in `ChatPanel.cs` (520) could no longer fit all content (7 checkboxes + 4 input fields, ~556px measured), so the bottom button row (Init Skill Data / Cancel / Save) was clipped and unclickable. Height raised to 600, and the content StackPanel is now wrapped in a ScrollViewer as a fallback — buttons stay reachable as more settings are added or under high-DPI scaling.

- **About window height** — Under high DPI (125%/150%) the disclaimer text wrapped without enough room. Height 300 → 380.

## [0.3.9] — 2026-06-14

Two prompt / caching-layer optimizations. No new features, no breaking changes.

### Changed

- **Thinking-language instruction strengthened** — 0.3.8's thinking-localization instruction was a single weak sentence appended at the end of a long prompt (measured: after installing 0.3.8, thinking was still English). Now merged into the reply instruction (one IMPORTANT governing both think + respond), reworded to a direct `Think in X` + explicit `NOT in English`, covering both the `reasoning` / `thinking` terms (GLM uses `reasoning_content`, Anthropic uses thinking blocks). The `GetThinkingInstruction` helper in `AiPrompt.cs` was removed; the logic moved into `BuildStablePrompt`.

- **Cache-hit optimization: messages' `cache_control` collapsed to a single breakpoint** — Previously `AiHistory.cs` `BuildTrimmedMessages` stamped `cache_control` on **every assistant message**, blowing up to dozens of breakpoints in long sessions (measured max: 49). Anthropic limits each request to 4; the excess breakpoints were silently ignored by the server → the corresponding history prefix was never cached → the next request retransmitted it in full (measured: 48% of requests had `cache_read=0`; cumulative uncached retransmission was 4.35M tokens — more than what the hits saved). Now only the **last assistant message**'s trailing block gets a single breakpoint (system + tools already take 2-3, total within the 4 limit), caching the entire history prefix. After a new message is appended, that prefix stays stable → hit.

## [0.3.8] — 2026-06-14

Thinking-process localization: added a "thinking localization" toggle. When enabled, the AI's extended thinking (reasoning) follows the MotionPerfect system language instead of defaulting to English.

### Added

- **Thinking-process localization toggle** — Previously `GetLanguageInstruction` only constrained the **reply language**, with zero constraint on thinking language → the model defaulted to thinking in English (the split of Chinese replies with English thinking). `AiPrompt.cs` `BuildStablePrompt` now appends a thinking-language instruction when the toggle is on (e.g. `conduct your internal reasoning (your thinking blocks) in Chinese`), with the language source identical to the reply language (`CurrentUICulture`). Toggle defaults to **on** (old users upgrading get the default when the config field is absent). Settings panel gets a new checkbox with 4-language copy (zh/en/de/fr). This is a prompt-layer nudge; mainstream models comply well but it's not 100% guaranteed, hence the toggle to disable.

## [0.3.7] — 2026-06-14

Against the official Anthropic extended-thinking docs, unified thinking-block handling to "pass back as-is", fully removing the URL-hardcoded distinction introduced in 0.3.6. All three providers (Anthropic / GLM / DeepSeek) actually share one convention.

### Changed

- **Removed `isRealAnthropic` URL hardcoding** — 0.3.6 used whether `_apiUrl` contained `anthropic.com` to distinguish real Anthropic vs GLM-compatible endpoints. Verified against Anthropic docs that the distinction is unnecessary.
- **`EnsureValidMessageSequence` removes "unsigned-thinking cleanup"** — Anthropic docs explicitly state "If sending back thinking blocks, pass everything back **as you received it**"; the root cause of the 400 "thinking blocks cannot be modified" is "reconstructing messages", not "missing signature". The previous signature-based cleanup was exactly the "reconstructing messages" behavior the docs call out. Real-Anthropic completion blocks carry a signature; GLM/DeepSeek completion blocks structurally have none — passing back as-is is correct for all three, and also eliminates the "message sequence repaired" log spam of 0.3.4/0.3.6.
- **`CallApiStream` removes `clear_thinking: false`** — A GLM-only parameter; real Anthropic `thinking` config rejects it (strict param validation). Can't add it unconditionally, and can't URL-hardcode the distinction, so removed. Multi-turn thinking context now comes from "passing thinking blocks back as-is" (the canonical tool-use multi-turn requirement — more fundamental than clear_thinking).
- **Stream-interrupted partial thinking no longer enters history** — `AiService.cs` flush path (stream-interrupt finalization) no longer writes unsigned partial thinking blocks into `result.Content`. Partial blocks never received `content_block_stop` (signature_delta is sent only right before stop) — the only true poison blocks. Caught at the source; no after-the-fact signature-based cleanup needed.

### Verification

- `AiOptimizationTests.cs` Phase-Thinking-2 flipped: unsigned thinking blocks now **should be retained** (pass back as-is), not removed.

## [0.3.6] — 2026-06-14

Fixed thinking handling against the official GLM thinking-mode docs (0.3.4 was based on an Anthropic-signature assumption, misapplied to GLM).

### Fixed

- **"Message sequence repaired" log spam + thinking all deleted** — `AiHistory.cs` `EnsureValidMessageSequence`'s "remove unsigned thinking" logic made conditional: only real-Anthropic endpoints (`_apiUrl` contains `anthropic.com`) clean up; GLM/compatible endpoints don't. GLM uses a `reasoning_content` field with **no signature concept** (confirmed by GLM docs, corroborated by 67 sessions with sig=0); thinking blocks have no signature by nature. 0.3.4's unconditional cleanup triggered every API request, deleting all thinking blocks + spamming logs, and meant 0.3.4's multi-turn thinking upgrade never took effect on GLM.

### Added

- **GLM Preserved Thinking** — `AiService.cs` `CallApiStream`'s thinking config adds `clear_thinking: false` for GLM/compatible endpoints (real Anthropic doesn't get it; it uses the signature mechanism and doesn't recognize this param). Per GLM docs: "the model can preserve the reasoning content of previous assistant turns in context… `clear_thinking: false`". With it, GLM preserves prior-turn reasoning and multi-turn thinking becomes truly coherent (previously, even when thinking blocks were passed back, GLM's default clear_thinking wiped them each turn).

## [0.3.5] — 2026-06-14

Trimmed the AI tool list (66→64), fixed a naming-inconsistency bug, revived dead-code tools.

### Fixed

- **`read_drive_params` naming bug** — The name given to the AI was `read_drive_params` (plural s), but the dispatch (DispatchTool) only recognized `read_drive_param` (singular); AI calls always returned `Unknown tool`. Unified to `read_drive_param` (consistent with `write_drive_param` and the case).
- **`rename_program` dead code revived** — Previously appeared only in WriteTools / DispatchTool case / Handler / HTTP API; `BuildToolDefinitions` lacked the definition, so the AI never saw or called it. Added the definition (params name + newName); the Handler was already implemented; the AI can now use it.

### Changed

- **`get_sysvars` removed from the AI list** — Redundant with `read_sysvar` (the latter reads any named system variable). Handler and ApiServer HTTP endpoint retained; external callers unaffected.
- **`open_oscilloscope` removed from the AI list** — After the AI opens the oscilloscope window there are no companion tools to read waveforms or operate it, so limited value. Handler and HTTP endpoint retained.
- **`task_get` removed** — `task_list` already returns all fields (id/subject/description/status); `task_get` adds nothing. Case, PureIoTools entry, TaskGet method, and related tests deleted.

## [0.3.4] — 2026-06-14

Fixed broken extended-thinking multi-turn context, and auto-compaction that never succeeded under GLM-5.2+thinking (long-term fallback to hard truncation losing context).

### Fixed

- **thinking signature retained end-to-end (multi-turn thinking no longer breaks)** — `AiService.cs` stream parsing now handles `signature_delta` (overwrite-style assignment, per the Anthropic extended-thinking spec) and writes the `signature` field when constructing thinking blocks. Previously signature was dropped entirely, so unsigned thinking passed back to the API was ignored and the model rethought from scratch each turn (same root as the historical "amnesia/incoherence" issues). `JavaScriptSerializer` serializes dicts preserving all keys, so save/load/trim carry signature naturally with no extra changes.
- **Auto-compaction never succeeded → fixed** — `AiHistory.cs` `CallCompactApi` rewritten as streaming: ① `stream:true` (the path validated for the main conversation; non-streaming was never validated); ② with thinking config when `_enableThinking` (prevent GLM-5.2-compatible endpoints from returning 400 or bare thinking blocks due to config inconsistency); ③ streaming accumulates only `text_delta` and skips `thinking_delta` (prevent thinking-as-first-block from yielding empty text); ④ HTTP non-2xx logs status code + error body to `perf_error.txt` (previously silent return null, undiagnosable). Before the fix all compact calls failed and fell back to hard truncation, bypassing tool-dedup / file-context-restoration optimizations entirely.
- **Small history falsely triggering compaction** — `AiHistory.cs` `TrimHistory` trigger changed from "count>100 or tokens>500K" to "tokens≥500K or (count>100 and tokens≥100K)". New constant `CountTriggerTokenFloor=100000`. Previously a 14K-token / 101-message small history also triggered compaction (then failed compact → hard truncation losing context).
- **Defense: remove unsigned thinking blocks** — `AiHistory.cs` `EnsureValidMessageSequence` gets a new scan at the end that removes unsigned thinking blocks (sources: stream-interrupt flush partial thinking, pre-upgrade old history), preventing "thinking blocks cannot be modified" 400 when passed back to the API. Detoxes and permanently writes back to history before every API request.

### Added

- **redacted_thinking block support** — `AiService.cs` stream parsing gets a `redacted_thinking` branch (`data` is given all at once in `content_block_start`, no delta event); Anthropic safety-filtered blocks are no longer silently dropped.

## [0.3.3] — 2026-06-14

Refined the batch multi-program task strategy: clarified that Plan Mode does not apply to batch same-type tasks, preventing the AI from entering Plan Mode and reading all programs at once to "draft a plan covering all programs" (context flooding).

### Changed

- **Batch tasks don't enter Plan Mode (prompt hard rule)** — `AiPrompt.cs` BATCH section gets a new rule: batch same-type tasks (fix/check all programs) are N **independent** subtasks needing no global design; **`enter_plan_mode` is forbidden** (it forces investigate-everything-first, which reads all programs into context — exactly the context flooding 0.3.2's BATCH rule forbids). Batch tasks go straight to `task_create` to build a list + execute one by one; Plan Mode is reserved for tasks that genuinely need global design (new-architecture refactor, multi-program linkage overhaul).
- **`enter_plan_mode` tool description supplemented** — `AiTools.cs` explicitly annotates avoiding it for "same-type operations on multiple independent items" (fix/check all programs) — these are independent subtasks and should go through the task system + per-item processing.

## [0.3.2] — 2026-06-14

Fixed "AI amnesia self-loop after loading an old session" — the user sent one instruction (e.g. "create 3 demos at once"), but the AI auto-looped the "opening + creation action" 4 times, with all 4 responses' `thinkingText` byte-for-byte identical (proof the model restarted from the same context each time). `chat_history/20260614_092608.json` data-layer verification: the UI `messages` had the same user instruction 4 times, while the `history` sent to the API was ultimately clean. Double root cause: the restore blob treated completed actions as pending, and history dedup didn't cover consecutive identical user text.

### Fixed

- **AI self-loop redoing after loading an old session (consecutive-duplicate user-text dedup)** — `AiHistory.cs` `EnsureValidMessageSequence` gets a new "second-and-a-half pass" scan (after user tool_result handling, before assistant tool_result backfill): deletes physically-adjacent user messages whose `content is string` and byte-identical, keeping the first. Self-loop / old dirty data / restore-accumulated consecutive duplicate user text is cleaned each API turn and permanently written back to `_conversationHistory` (in-place repair, same mechanism as 0.2.16). The "adjacent" judgment + `is string` guard ensure no false positives on genuine user resends (separated by assistant → no trigger) or tool_result messages (content is List). 3 new Phase2-* unit tests added.
- **Restore blob misled the model into redoing completed actions** — `AiSession.cs` `LoadSession`'s session-restore blob copy changed from the vague "restored context" to explicit bilingual copy "[上一会话已完成的工作记录（仅作上下文，请勿重复执行）]" / "[Record of work completed in the previous session (context only — do not re-execute)]"; tool-call records' label changed from `System` to `Tool` so the model sees which actions were already executed; the injected assistant reply is also bilingual, clarifying "recorded, won't redo, continue from current state".
- **UI message accumulation when re-loading a session** — `ChatPanel.cs` `LoadLastSession` adds `_messages.Clear()` after `_ai.LoadSession`, consistent with `LoadSessionMessages`, avoiding duplicate UI message buildup when re-loading the same session.
- **New conversation didn't exit Plan Mode** — `AiSession.cs` `StartNewSession` only cleared history without resetting `_planMode`, so after "New conversation" the previous session's Plan Mode lingered: write tools still rejected, UI banner didn't disappear. Now resets `_planMode` and fires `OnPlanModeChanged(false)` (invoked outside the lock to avoid UI-callback reentrancy).

### Added

- **Batch / multi-program tasks processed one by one (prompt hard rule)** — `AiPrompt.cs` gets a new `BATCH / MULTI-PROGRAM TASKS` section: for same-type multi-program operations like "fix/check all programs", the AI must process **one by one** (read → patch → compile-verify → next), forbidding reading all programs into context first then batch-editing — the latter is exactly the context-overflow trigger for the amnesia/loop bug. Works with the `task_*` system to track progress.
- **AI tools support hexadecimal addresses** — `AiJson.cs` adds `GetHexInt`/`GetHexLong` (parses `0x4000`; without prefix tries decimal first, falls back to hex); `AiTools.cs`'s `read_vr`/`write_vr`/`read_table`/`write_table`/`read_drive_params`/`write_drive_params`/EtherCAT SDO `index`/`subindex` use hex parsing, consistent with `ApiServer.TryParseAddr`. The AI can now use `0x` addresses directly for VR/TABLE/drive/SDO.
- **Plugin startup warmup** — `TrioAIPlugIn.cs` adds WPF/JIT Prewarm to warm up slow first-load assemblies, shortening cold start; cleans up the `PerfLog`/`AssemblyLoad` debug code left from the previous perf investigation.

### Changed

- **Chat serialization guard** — `AiService.cs` adds a `_chatRunning` defense layer, a backstop preventing other entry points from concurrently calling `Chat` (normally intercepted by `ChatPanel._isProcessing`).
- **max_tokens truncation fallback** — `AiService.cs` each message starts at the default max_tokens, temporarily upgraded only when truncated this turn, avoiding never falling back after upgrade for the whole session.
- **Tool cancellation backfills a tool_result stub** — `AiService.cs` when canceled during tool execution, backfills a stub tool_result + sentinel for already-issued tool_use, so the next user isn't adjacent to the previous user/tool_result block (Anthropic requires strict alternation), consistent with the API-stream cancellation cleanup.
- **/api/status version made dynamic** — `ApiServer.cs` from hardcoded `1.7` to reading the assembly version.
- **breakpoint DELETE param validation** — `ApiServer.cs` DELETE breakpoint requires the `line` param (1-based); missing returns error.

## [0.3.1] — 2026-06-14

4 bug fixes found by Phase-1 testing. Focused on the severe amnesia loop when "reading 10 codes for statistics".

### Fixed

- **multi-write parallel deadlock** — `ChatPanel.cs` `ShowInlineConfirmation` used a single-instance field `_confirmTcs`; under parallel scenarios multiple `Task.Run`s `BeginInvoke` into the dispatcher queue simultaneously, the latter overwriting the former, so when the user clicks Allow only the last tcs unlocks and the earlier ones never complete → `Task.WaitAll` deadlocks. Added a `_confirmLock` field; `ShowInlineConfirmation` is fully wrapped in `lock` to force serialization.
- **Plan Mode approval popup → bubble** — `ChatPanel.cs` `ShowPlanApproval` drops `MessageBox.Show`, reusing the existing `_confirmPanel` + `_confirmTcs` + `OnConfirmAllow`/`OnConfirmReject` mechanism. Approval buttons embedded at the bottom of the chat UI, same panel style as write confirmation. Also `lock (_confirmLock)` to prevent conflict with write confirmation.
- **`_recentReadFiles` capacity too small caused post-CompactHistory amnesia** — `AiSession.cs` `MaxRestoredFiles` 5 → 20, `MaxRestoredFileChars` 4000 → 6000. Originally when reading 10 codes for stats, the LRU kept only the last 5, the first 5 were evicted, and after CompactHistory they couldn't be restored.
- **microCompact emptied read_source content causing a 90x re-read loop** — `AiHistory.cs` microCompact logic keeps the last N=5 tool_result full contents, with an exception list of only `lookup_command` / `read_skill` — **`read_source` was not on it**. When reading 10 different files, the first 5 tool_results were emptied to `"[Old tool result content cleared]"`; the AI saw the cleared hint and re-read, forming a vicious cycle. Added `read_source` to the `keepRecent` exception (read_source already dedups by same-file, won't accumulate indefinitely).

## [0.3.0] — 2026-06-14

Phase-1 optimization: filled in the scaffolding for autonomous agent programming (against cc-haha missing items), without touching the existing architecture or adding new dependencies. 9 backend changes + Plan Mode UI + 7 new unit tests.

### Added

- **Parallel tool execution** — `AiTools.cs` adds a `PureIoTools` HashSet (lookup_command / read_skill / discover_skills / task_* / enter_plan_mode / exit_plan_mode, 9 total); these tools bypass `DispatcherHelper.Invoke` and execute directly; `AiService.cs` agentic loop changes `foreach` serial execution to `Task.Run` + `Task.WaitAll` + collecting tool_results in the original tool_use order. True parallelism applies only to pure-IO tools (other tools still go through the UI-thread serialization; the MP API is STA).
- **API error classification & retry** — New `RetryableApiException` (carries StatusCode + RetryAfterSeconds); `CallApiStream` throws this for 5xx / 429 / IOException / HttpRequestException; new `CallApiWithRetry` does exponential-backoff retry (1s/2s/4s, 3 total). Retry-After header parsing. Other 4xx errors don't retry. The Chat-loop call site changed to `CallApiWithRetry`.
- **Task/Todo system** — 4 new tools `task_create` / `task_update` / `task_list` / `task_get`. `AiSession.cs` adds a `_tasks` List + `_tasksLock`. The AI can self-track multi-step task progress; task state doesn't enter conversation history (doesn't pollute context).
- **Plan Mode** — New `enter_plan_mode` / `exit_plan_mode` tools + a `_planMode` state field. In Plan Mode all `WriteTools` calls are rejected directly (returns BLOCKED hint). `exit_plan_mode` requests user approval via the `OnConfirmPlan` callback; if the UI hasn't hooked the callback it defaults to approve to avoid AI deadlock.
- **discover_skills tool** — Lists all markdown skills' name/description/when_to_use/category; the AI perceives available skills at the tool-selection stage, more efficient than read_skill trial-and-error.
- **Plan Mode UI** — `ChatPanel.cs` adds an orange status bar (shown below the toolbar when Plan Mode is active) + a Plan approval MessageBox dialog (shows the AI-submitted plan text, Yes/No). Hooks `OnPlanModeChanged` + `OnConfirmPlan` callbacks.
- **Unit tests** — `AiOptimizationTests.cs` adds 7 Phase1-* tests: PureIoTools dispatch, 7 new tool registrations, MaxTurns/TokenBudget constants, RetryableApiException + exponential backoff, Task CRUD, Plan Mode interception + auto-approve, EnsureValidMessageSequence orphan tool_result cleanup. The `P1-1` tool-count assertion updated 59 → 66.

### Changed

- **Loop exit conditions** — `AiService.cs` `MaxTurns` from hardcoded 20 to constant 50; new `TokenBudgetLimit = 400_000` chars (≈100K tokens), checked each turn inside the loop, over the threshold first tries `TrimHistory`, still over prompts the user to start a new session.
- **read_source tool description** — Strengthened the pagination-guidance tone, explicitly telling the AI "the first chunk is not the full file; you must keep reading with startLine/endLine".
- **CompactHistory failure prompt** — When summary fails and falls back to hard truncation, the UI shows `⚠ Auto-summary failed, fell back to hard truncation (latest 30 messages retained)`, no longer silently losing context.

### Fixed

- **TrimHistory hard-truncation fallback** — Previously the hard-truncation path only reset `_conversationHistory` without calling `EnsureValidMessageSequence`, possibly leaving orphan tool_results that triggered repair log spam on every subsequent API request. Now calls cleanup once immediately after hard truncation.

## [0.2.16] — 2026-06-13

### Fixed

- **Root-fixed "message sequence repaired" repeated log spam (persistent in-place repair)** — 0.2.15 only sanitized at CompactHistory/TrimHistory cut points to prevent producing new orphan tool_results, but `EnsureValidMessageSequence` previously only repaired on the request copy `messages` **without writing back to `_conversationHistory`**. Once the history already had bad data (legacy from old versions / old session files loaded by LoadSession / sentinels stuffed on cancel, etc.), every API request repeated the "copy bad history → repair copy → notify → send copy → next time still bad original" cycle. Now `BuildTrimmedMessages` entry does in-place repair on `_conversationHistory` itself: the first trigger repairs the history and notifies once; subsequent requests have `repaired=false` and don't notify. The trailing repair on the messages copy is retained as a fallback.

## [0.2.15] — 2026-06-13

### Fixed

- **CompactHistory/TrimHistory cut-point sanitization, root-fixing the frequent "message sequence repaired" log spam** — `AiHistory.cs` previously used `compactEnd = Count - MaxRecentKeep` hard-cut by count; if the cut landed between `assistant(tool_use_X) → user(tool_result_X)`, `assistant(tool_use_X)` got pressed into the summary while `user(tool_result_X)` survived as an orphan tool_result. `EnsureValidMessageSequence` doesn't write back to `_conversationHistory`, so every subsequent API request repeated the repair and spammed "⚠ History repair: message sequence repaired". New helper `IsUserToolResultMessage`: at the CompactHistory cut point, if it's an orphan `user(tool_result)`, `compactEnd++` presses it into the summary too; TrimHistory skips `user(tool_result)` during scanning so the cut doesn't land on an orphan pair. No more orphan tool_results produced at the source.

## [0.2.14] — 2026-06-13

### Skill-loading mechanism (referencing the claudecodefx cc-haha design)

- **Markdown skill frontmatter gets a `when_to_use` field** — `AiSkills.cs`'s `ParseSkillMd` parses `when_to_use`/`whentouse`/`when-to-use` spellings; `MdSkillEntry` gets a `WhenToUse` field. Lets the AI see "when to use this skill" guidance right in the Available Skills list, without having to read_skill first.
- **Available Skills list token budget + truncation** — New `FormatSkillListing`/`FormatSkillEntry`: whole list capped at 8000 chars (~1% context), single entry capped at 250 chars, over budget truncated to name-only by even split. Guards for future skill-count growth. The safe-coding full-embed behavior is unchanged (security protection, never downgraded).
- **`read_skill` tool description gets a BLOCKING REQUIREMENT** — `AiTools.cs`'s read_skill description adds the hard rule "when the user request matches a skill's 'Use when:' description, you must call read_skill before writing code", and notes that already-embedded skills (like Safe Coding Rules) don't need re-reading. Fixes the previous "AI sees the skill but doesn't proactively call it" issue.
- **`read_skill` results retained across microCompact** — `AiHistory.cs`'s `BuildTrimmedMessages` adds `read_skill` to the `keepRecent` whitelist (same treatment as `lookup_command`); microCompact no longer empties the body of already-read skills.

### Prompt

- **Reference Libraries section gets category lists** — `BuildSkillsCatalog` normalizes each library's (triobasic/iec/plcopen) `type` field then takes the top 8 categories (e.g. "Axis Parameter (221), System Command (86), ..."), letting the AI know what categories exist in each library, inferring more precise `lookup_command` query terms from "task → category name". Computed from index.json at runtime, auto-updates with library content. New `NormalizeType` (strip trailing period / strip parenthetical notes / collapse whitespace) and `SummarizeTypes` (top 8 by entry count desc) helpers. +~95 token cost.

## [0.2.13] — 2026-06-13

### i18n

- **`AiHistory.cs` 6 system-prompt strings localized** — Filled in the parts 0.2.12's localization missed: `⚠ TrimHistory triggered` (trim stats), `Auto-compacting conversation history...` (compaction start), `Compacted into summary` (compaction done), and 3 `⚠ History repair: ...` history-repair hints (skip non-user messages / no-user-message placeholder insert / message sequence repaired). Reuses the `Lang.L(zh, en)` helper introduced in 0.2.12.

## [0.2.12] — 2026-06-13

### i18n

- **New `Lang.L(zh, en)` helper** — `ChatPanel.cs` reuses the existing `LangCode` detection, returning the string for the current UI language. One-shot system messages need only zh/en translations; de/fr/es/it/pt-BR/hu/ro/ru/sv etc. fall back to en.
- **`AiService.cs` 11 user-visible strings localized** — Including `(Reached maximum iterations)`, `[Compile Error]`, `Backup saved`, `API key not configured`, `Network error`, `Failed to call AI API`, the `max_tokens` upgrade hint (bidirectional fix: some messages after 0.2.8 were Chinese-only, showing Chinese even in English mode), `API Error` (3 places), `已备份`, `ERROR`.
- **Fixed `(Reached maximum iterations)` showing English in Chinese MP**.

## [0.2.11] — 2026-06-13

### Docs

- **Fixed `API.md`'s `patch_source` doc** — Previously still the old `{action,line,content}` format (missed when CHANGELOG 0.1.40 changed the format); the actual code had long been the `{old_string,new_string}` text-replace mode. Also added the `pouName` param description, the "program must already exist" precondition, and the `old_string` uniqueness & empty-string-append semantics.

## [0.2.10] — 2026-06-13

### Prompt

- **Made `patch_source` preconditions explicit** — `AiPrompt.cs`'s "WRITING LARGE PROGRAMS" section gets a hard rule: `patch_source` only applies to already-existing programs; new programs must use `write_source` (with `create_program` first if needed). `AiTools.cs`'s tool description also clarifies "REQUIRES the program to already exist — cannot create new programs". Fixes the AI preferring patch_source when the file doesn't exist and failing.

### Diagnostics

- **Removed the `LoadSession` diagnostic log added in 0.2.8** — The amnesia bug was traced to old session files missing the `history` field (saved pre-0.2.x); the user chose to just delete old sessions, no need to modify LoadSession fallback logic for this.

## [0.2.8] — 2026-06-13

### Diagnostics

- **`LoadSession` detailed diagnostic logging** — Tracing the root cause of `_ai.LoadSession` failing to load history causing AI amnesia. Logs write directly to `%APPDATA%/TrioAI/perf_error.txt`, recording each step's status for file read, deserialization, history load, summary injection, plus exception type + message + stack. To be cleaned up after verification.

## [0.1.39] — 2026-06-12

`EnsureValidMessageSequence` adds cross-message tool_use ID dedup.

### Fixed

- **Cross-message tool_use ID dedup** — When two assistant messages contain tool_use with the same id, the duplicate is dropped. The API requires tool_use ids to be globally unique; duplicates cause the request to be rejected.
- **Orphan tool_result check optimization** — Switched to a pre-collected `validToolUseIds` set for judgment, no longer traversing preceding messages per-item, better performance.

## [0.1.38] — 2026-06-12

Rewrote the message-sequence defense logic, referencing claudecodefx's ensureToolResultPairing.

### Fixed

- **New `EnsureValidMessageSequence`** — Four-pass scan to repair the messages array:
  1. Remove orphan tool_results (no corresponding assistant tool_use)
  2. Insert synthetic error results for tool_uses missing a tool_result
  3. Ensure the first message is a user message (skip leading assistant)
  4. Empty-array fallback: insert a placeholder user message instead of clearing
- **Fixed `messages:[]` causing a 1214 error** — Old logic did `messages.Clear()` when all messages were orphan tool_results; an empty array is also illegal. Now inserts placeholder text instead.

## [0.1.37] — 2026-06-12

Fixed the 1214 API error: an orphan tool_result as the first message makes the params illegal.

### Fixed

- **`BuildTrimmedMessages` defense strengthened** — Original defense only handled the first-message-is-`assistant` case. When `TrimHistory` truncation removed both the `assistant` and the preceding `user`, it left an orphan `tool_result` as the first message; the API requires `tool_result` to immediately follow an `assistant`'s `tool_use`, hence a 1214 error. Now detects and skips orphan `tool_result` until a legal first message is found.

## [0.1.36] — 2026-06-12

Fixed the `write_source` controller validator false-positive on DIM variable names.

### Fixed

- **`ValidateByController` skips DIM-variable false positives** — The controller `ValidationService` line-by-line validation doesn't understand DIM context, flagging user-declared variable names (e.g. `conv_speed`, `cycle_no`) as illegal commands, blocking `write_source`. Now pre-scans DIM/LOCAL/GLOBAL declared variable names before validation and skips errors containing these variable names.
- **New `ScanDimVariables()`** — Parses DIM statements to extract variable names (supports array subscripts and AS clauses).
- **New `IsDimVarError()`** — Matches DIM variable names in error messages.

## [0.1.35] — 2026-06-12

Added system-prompt rule: forbid whole-file rewrite on patch failure.

### Added

- **System prompt gets a "NEVER REWRITE ENTIRE FILES" rule** — When `patch_source` fails, forces the AI to re-read the source, analyze the mismatch cause, and retry the patch; after at most 3 retries asks the user. Explicitly forbids falling back to `write_source` to rewrite an entire existing program.

## [0.1.34] — 2026-06-12

lookup_command dedup adds a library dimension to distinguish.

### Fixed

- **`BuildTrimmedMessages` dedup key adds library** — Original dedup key was `query+full`; same-named commands in different libraries were falsely judged as duplicates. Now key is `query+full+library`, deduped per library.

## [0.1.33] — 2026-06-12

Fixed lookup_command dedup logic: a brief query shouldn't block a subsequent full query.

### Fixed

- **`BuildTrimmedMessages` dedup changes to query+full combo** — Originally deduped by query only, so querying brief first then full replaced the full HTML with a placeholder. Now full=true and brief are different keys deduped separately.
- **`TryDedupLookupCommand` full→brief reverse dedup** — A full=true result can substitute a brief query (reverse dedup), but a brief can't block a subsequent full=true query.

## [0.1.32] — 2026-06-12

Fixed `GetStr`'s bool-value case normalization, root-fixing the dedup vs tool-execution behavior inconsistency.

### Fixed

- **`GetStr` bool normalization** — `JavaScriptSerializer` deserializes JSON `true` to C# `bool true`, whose `.ToString()` yields `"True"` (capital T). Tool-execution checking `== "true"` fails, returning a lightweight result; dedup uses the same `GetStr` and also gets `"True"`, and two `"True"`s happen to match, causing calls with different `full` params to be wrongly deduped. Now `GetStr` uniformly returns lowercase `"true"` / `"false"` for bool values; tool and dedup behavior fully consistent.

## [0.1.31] — 2026-06-12

Fixed dedup param comparison vs tool-execution behavior inconsistency.

### Fixed

- **Dedup uses `GetStr` instead of `NormParam`** — `NormParam` normalizes bool `true` to `"true"` (lowercase), but tool execution uses `GetStr` → `.ToString()` yielding `"True"` (capital T), so `full=true`(bool) was treated as non-full by the tool but as full by dedup, producing wrong dedup. Now dedup and tool use the exact same value extraction.
- **`full` exact match** — `"True" != "true"` doesn't match (reflects the tool's actual judgment); `"true" == "true"` matches.
- **`library` null vs value doesn't match** — `null != "iec"` doesn't match, avoiding cross-library wrong dedup.

## [0.1.30] — 2026-06-12

Added dedup diagnostic logging.

## [0.1.29] — 2026-06-12

Fixed illegal messages params causing API 1214 errors.

### Fixed

- **`BuildTrimmedMessages` defensive check** — After history is abnormally truncated the messages array may start with the `assistant` role, violating the Anthropic Messages API requirement. Added logic to skip leading assistant messages, ensuring the first is always `user`.
- **`TrimHistory` truncation fallback strengthened** — Original logic only matched `user + string content` messages; in the agentic loop all user messages are `tool_result` (list content), so no truncation point was found. Changed to fallback-match any user message, avoiding accidentally keeping an assistant-led history.
- **`TrimHistory` diagnostic log** — On trim trigger, outputs message count and token estimate to help trace abnormal triggers.

Fixed lookup_command dedup param comparison bug.

### Fixed

- **`NormParam` normalizes param comparison** — JavaScriptSerializer returns `"True"` for `full=true` (bool) and `null` for absent keys, so `full=""` and `full="True"` mismatched on comparison. New `NormParam` helper: `bool` → `"true"/"false"`, `string` → lowercase, missing → `""`; unified then exactly compared.
- **Skip the `GetDictValue` indirection** — Directly `b.TryGetValue("input") as Dictionary` to get the historical tool_use's input, avoiding the serialize/deserialize path losing type info.

## [0.1.27] — 2026-06-12

API request token optimization (reduce ~34% duplicate data transfer).

### Optimized

- **P0: lookup_command runtime dedup** — The AI repeatedly calls `lookup_command` for the same command in a session (e.g. MOVELINK queried 9+ times, each returning ~16KB HTML). New `TryDedupLookupCommand` intercepts at the `ExecuteTool` entry: when scanning `_conversationHistory` finds a prior successful call with the same query+library+full, returns a ~200-byte reference hint instead of reloading the full HTML. Estimated ~3.7MB of duplicate data saved.
- **P1: Tool-definition static cache** — `GetToolDefinitions()` rebuilt 59 tool schemas (~17KB) each call. Changed to build once and cache as `_cachedToolDefs`, returning a shallow copy thereafter. Eliminates per-API-request duplicate object allocation.
- **Context-compaction compatible** — `TryDedupLookupCommand` scans the raw `_conversationHistory`; after `CompactHistory` replaces old messages with summary text the scan misses, naturally falling back to normal execution, no side effects.

## [0.1.19] — 2026-06-11

The UI toolbar adds real-time message-count and token-estimate display (Msgs: N ~XK tokens).

## [0.1.18] — 2026-06-11

History management changed from hard-budget truncation to auto-compaction (referencing Claude Code).

**Changed**:
- History token budget raised from 30K chars (~7.5K tokens) to 500K chars (~125K tokens), fully utilizing the model's context window
- Max retained messages raised from 30 to 100
- New auto-compaction: when over budget, calls the AI to summarize old messages (instead of directly discarding); the latest 30 messages are kept unchanged
- Auto-compaction summary retains key context like user intent, code changes, bug fixes, work state
- On summary failure, still goes through the original truncation logic as a fallback

## [0.1.17] — 2026-06-11

patch_source operation format rewritten from `{action,line,content}` to the `{old_string,new_string}` text-replace mode.

## [0.1.16] — 2026-06-11

patch_source rewritten to the old_string/new_string text-replace mode (referencing the Claude Code FileEditTool).

**Changed**:
- `patch_source` operation format changed from `{action, line, content}` to `{old_string, new_string}` — locates the replacement via exact text match, fully independent of line numbers
- `old_string` must match uniquely in the source; on non-unique returns an error hint with extra context
- Supports Trim-tolerant fuzzy matching, tolerating trailing-whitespace differences
- Empty `old_string` appends `new_string` at the end of the file
- `patch_source` response adds an `operations` array, returning each op's status (replaced / skipped / appended)

**Background**: The old line-number-based patch mechanism often mis-targeted edits due to the AI's line-number drift. The old_string/new_string mode fundamentally eliminates line-number offset issues.

## [0.1.15] — 2026-06-10

Program-type awareness (TrioBASIC / IEC ST / PLCopen dialect distinction).

**Changed**:
- `GetProgramDialect(name)` returns `"triobasic"` / `"iec"` / `"unknown"`
- `read_source` response adds `type` and `dialect` fields
- `write_source` / `patch_source` only run TrioBASIC validation on triobasic programs; IEC programs skip it
- `LoadIndex` loads all three libraries (triobasic + iec + plcopen)
- `lookup_command` adds a `library` param to limit the search scope
- `BuildProjectContext` lists each program name and type (replacing aggregate stats)
- System prompt adds a `PROGRAM TYPE AWARENESS` rule section

**Fixed**:
- IEC ST programs no longer false-blocked by the TrioBASIC validator
- `PROGRAM MAIN` no longer wrongly written into an IEC POU (the AI can now see program types)
- `lookup_command` can be library-scoped, avoiding cross-language matches

## [0.1.14] — 2026-06-10

lookup_command three-layer mechanism + 192KB budget.

**Changed**:
- `lookup_command(query)` returns Layer 2 by default: name + signature + description (~500 bytes/entry)
- `lookup_command(query, full=true)` returns Layer 3: the full HTML doc (192KB total cap, truncated proportionally)
- Signatures are dynamically extracted from index.json's desc field (triobasic ~25% have signatures, the rest only name+desc)

Future plans:
- i18n expansion (more languages)
- Program-execution history / audit log
- HTTP API auth (prevent other processes from accidentally calling)
- Multi-controller switching support
- IEC breakpoint line→CodeElement reverse inference (currently SetBreakpoint must be set manually in the MP UI)
- regex hard-block VB patterns (`Dim`/`Function...End Function`/`Class`/`Math.`/`Console.`) — currently rule accuracy is insufficient, not yet enabled

## [0.1.13] — 2026-06-10

Removed Phase 1 (all-caps identifier whitelist validation), keeping only Phase 2 (function-call signature validation).

**Background**: Phase 1 caused heavy false-positives (user program names like `MOVE_DEMO:` were treated as hallucinated commands and blocked), the AI fell into an infinite loop triggering `(Reached maximum iterations)`. The cost of false-positives far exceeds false-negatives (the compiler is the backstop).

**Changed**:
- Removed `_reAllCapsIdentifier`, `_reLineComment` comment stripping + all-caps scanning logic
- Removed `IsAllUpperIdent()`, `GetProjectIdentifiers()` and other helpers
- Fixed `_reLineComment` regex (`[^*\r\n]` → `[^\r\n]`), but removed together with Phase 1
- Phase 2 (`Name(args)` function-call signature validation + unknown-command detection + param-count check + read-only-function assignment check) unchanged

**Defense layers**:
- Layer 1: System-prompt rules
- Layer 2: Phase 2 function-call validation (retained this time)
- Layer 3: TrioBASIC compiler (backstop)

## [0.1.12] — 2026-06-10

`lookup_command` repeat-query dedup version (token optimization).

### Optimized

- **Auto dedup of repeat queries** — In long conversations the AI often calls `lookup_command` for the same command multiple times (e.g. first queries `MOVE` to write code, then 5 turns later queries `MOVE` again to check syntax). Each returned HTML doc is ~16KB; repeating N times = N × 16KB of duplicate tokens. This adds a dedup layer at `BuildTrimmedMessages` (request-assembly stage):
  - Traverse all `lookup_command` `tool_use` blocks in history, group by `query` param (case-insensitive)
  - The **first** call of each query keeps the full content (HTML command doc)
  - Subsequent same-query `tool_result` contents are replaced with a ~200-byte reference placeholder:
    ```
    [Duplicate of lookup_command("MOVE") — full content preserved at the first
     call earlier in this conversation. Reference that occurrence instead of
     asking again.]
    ```
  - `tool_use_id` stays unchanged, pairing intact, API still works

### Benefit

Suppose 30 turns have 8 distinct commands each queried 2-3 times:
- Before: ~20 × 16KB = 320KB
- After: 8 unique × 16KB + 12 duplicates × 0.2KB ≈ 130KB
- **Saves ~60%** of lookup-related tokens

### Design tradeoffs

- **Only dedup `lookup_command`** — `read_source` also repeats, but the content changes after the user edits the source; `lookup_command` is a read-only static reference library, idempotent, safe.
- **The first must keep full content** — Can't empty all duplicates; at least 1 full copy must remain so the AI can look back for the syntax.
- **Replace with a reference, not empty** — The reference text explicitly tells the AI "it was queried before, look back", avoiding the AI confusedly re-querying.
- **Case-insensitive** — `MOVE` / `move` / `Move` count as the same query.
- **Dedup only at request-assembly stage** — UI/log/chat_history still record the full tool_result, for auditing and review.

## [0.1.11] — 2026-06-10

v0.1.10 validator-whitelist-pollution fix (32 end-to-end tests 100% pass).

### Fixed

- **IEC/PLCopen library polluting the TrioBASIC whitelist** — `EnsureValidationIndex` and `LoadIndex` both traversed all subdirs under `skills/*/`, so `skills/iec/AO-printf.html` got added to `_triobasicIds` → the AI writing `Printf()` was judged legal TrioBASIC. Changed to scan only `skills/triobasic/`; IEC functions (printf / AO-printf etc.) no longer leak into the whitelist.
- **`WAITS` / `DEFAULT` keywords not in the whitelist** — `WAITS` (wait-for-sync, distinct from `WAIT UNTIL`) and `DEFAULT` (the `SELECT CASE DEFAULT` branch) have no standalone HTML file and aren't in `_builtinKeywords`, so legal code `WAITS` and `SELECT CASE VR(0) CASE DEFAULT ... END SELECT` were false-blocked. Added to `_builtinKeywords`.

### Verification

32 end-to-end tests 100% pass:
- **12 legal TrioBASIC**: FOR/NEXT, WHILE/WEND, BASE+MOVE, VR/TABLE read-write, IF/ELSEIF/ELSE, SELECT CASE DEFAULT, GOSUB/RETURN, WAITS, SIN/COS/ABS, RND, nested function calls — none false-blocked
- **10 LLM-hallucination commands**: Sleep/Delay/Random/Foobar/Printf (incl. lowercase/uppercase variants)/WriteLine/Console.WriteLine/Math.Sqrt — all blocked
- **4 param errors**: ABS multi-param, SIN multi param, MOVE/BASE no-param — all blocked
- **3 assignment errors**: assigning to SIN/ABS/RND — all blocked
- **3 boundaries**: empty string, pure comment, REM comment — all pass

## [0.1.10] — 2026-06-10

v0.1.9 validator three-bug fix.

### Fixed

- **Validator read the wrong field name → entire validation disabled** — v0.1.9's `write_source` block read the `code` field, but the tool schema is actually `sourceCode`; `patch_source` read `new_line` / `new_content` / `line`, actually `content`. So the validator always got an empty string and never blocked. Changed to read the correct field names.
- **SetArgCount counted optional params as required** — TrioBASIC doc's optional-param form is `axis0[, axis1[, axis2[, ...]]]`; the old impl split by `,` then judged `StartsWith("[")` per item, but `[` appears *after* the param name, so `axis0[` `axis1[` `axis2[` were all counted required → `BASE(0)` / `MOVE(100)` were false-blocked (said to need ≥4 / ≥5 params). Changed to: take the part before the first `[` as required, all after as optional.
- **VR / TABLE and other system vars falsely judged non-assignable** — Pattern 1 `value = NAME(...)` hit then defaulted `IsAssignable=false`, but VR / TABLE are bidirectional (read-write); `VR(0) = 100` is legal TrioBASIC. Changed to: default allow assignment; only block assignment for explicitly `_knownReadOnly` pure functions (`ABS` / `SIN` / `COS` / `SQRT` / `RND` / string functions etc. ~30).
- **Unknown call `Name(args)` not in the index wasn't blocked** — Old Phase 2 only did signature validation on functions matched by `_signatures`, directly `continue`-skipping hallucinations like `Foobar(1,2)` (assuming Phase 1 handled it). But Phase 1's all-caps regex didn't match `Foobar`, causing a miss. Changed to: a `Name(args)` form not in `_triobasicIds` (containing all of VR/TABLE/ABS/MOVE...) is judged a hallucination.

### Added

- **HTTP endpoint `POST /api/validate_basic`** — Directly validates a TrioBASIC snippet, returns `{ok, errors}`. Used for: debugging validation rules, CI regression tests, batch validation without going through the AI. body: `{"code": "..."}`, response: `{"ok": true/false, "errors": [...]}`.

### Verification

15 end-to-end tests pass 14 (the last one `Dim x As Integer` is pure VB syntax with no parens, needs a regex blacklist to block, recorded under optimization directions).

## [0.1.9] — 2026-06-10

TrioBASIC pre-write whitelist + signature-validation version.

> ⚠️ **This version has a severe bug — the validator read the wrong field name so the entire validation never fired.** Please upgrade to [0.1.10].

### Added

- **Phase 1: identifier-whitelist validation** — `write_source` / `patch_source` call `ValidateTrioBasicCode` before writing, scanning all `Name(...)` call forms and `[A-Z_]+` all-caps identifiers in the code; anything not in these three classes is blocked: (1) TrioBASIC built-in keywords (IF/FOR/WHILE/DIM etc.); (2) the `lookup_command` index (806 entries + 180 HTML supplements); (3) variables the user has already declared in the current project. The AI writing `Foobar(...)` hallucinations immediately gets `BLOCKED by TrioBASIC validation: Unknown: ['FOOBAR']`, forcing a re-`lookup_command`.
- **Phase 2: signature parsing + param validation** — Parses each command's min/max param count and assignability from `index.json`'s desc field (assignable `x = ABS(...)` legal, `SIN(...) = 0` illegal), matching 3 signature patterns: `value = NAME(...)`, `NAME(...)`, `NAME arg1, arg2`. Over/under-param immediately blocked (e.g. `ABS(1, 2)` blocked: `got 2, max 1`).
- **README "Optimization Directions" section** — Lists 6 identified-but-unimplemented optimizations (incl. why regex-blocking VB patterns is deferred).

### Design tradeoffs

- **Whitelist first, blacklist deferred** — `lookup_command` whitelist + signature validation is more stable than a regex blacklist: the whitelist is based on a real command library (parsed from CHM), accurate coverage; a regex blacklist has to enumerate VB/QBasic patterns, and more rules mean more false-positives (e.g. `Dim` is also legal in TrioBASIC). Whitelist blocking first; regex once rules are polished.
- **Block only writes, not reads** — Validation runs only at the `write_source` / `patch_source` entry; `read_source` / `search_code` aren't validated. The AI can freely explore code during thinking (including reading the user's buggy code), forced only at the "land to disk" step.
- **User-variable whitelist built dynamically** — At validation, traverses all programs in the current project's assignment left-sides, extracting user variable names into the whitelist. So the AI writing code referencing global variables in other programs isn't false-blocked.
- **Actionable error messages** — On block, returns `BLOCKED by TrioBASIC validation:` + one concrete reason per line (`Unknown: ['FOOBAR']` / `L1: ABS got 2, max 1`); the AI can directly fix and retry.

## [0.1.8] — 2026-06-10

TrioBASIC-dialect-confusion defense strengthened.

### Improved (AI_INSTRUCTIONS.md / DefaultPrompt)

- **Dialect constraint moved to the top of the system prompt** — Previously `STRICT TRIOBASIC SYNTAX COMPLIANCE` was mid-prompt, diluted in long conversations. Now right after `## Capabilities`, ensuring the AI reads it before entering work mode.
- **Added a 22-line few-shot wrong/right contrast table** — Previously only listed anti-examples (`Dim`, `Function...End Function`); the LLM's training data has far more VB/QBasic than TrioBASIC, so just saying "don't" isn't enough. Now each line is explicit `WRONG (other BASIC) → CORRECT (TrioBASIC)`, covering all common confusion points: var declarations / function definitions / control flow / exceptions / IO / math / type annotations / comparison operators / comments.
- **Added an AFTER-WRITE SELF-CHECK mandatory self-review flow** — The prompt previously only said "MANDATORY before writing", with no reverse-check after the AI writes. Now adds a 5-step self-check: (1) list all commands (2) judge whether each was looked up (3) look up any not (4) don't submit if not found (5) re-check patterns against the table.
- **Removed the mid-prompt duplicate `STRICT TRIOBASIC` and `confusions` sections** — Constraints now concentrated at the top, avoiding scattered attention.

Note: DeployAIInstructions overwrites `%APPDATA%\TrioAI\AI_INSTRUCTIONS.md` each startup, so users get the new constraints on next MP launch without manual sync.

## [0.1.7] — 2026-06-10

Code-quality regression fix (v0.1.5's token optimization was too aggressive).

### Fixed

- **MaxToolResultLen 8000 → 16000** — 8000 truncated 11% of HTML command docs (98 total > 8KB), all high-frequency complex commands (FRAME 119KB, ETHERCAT 51KB, REGIST 47KB, MS_BUS 47KB, MODBUS 38KB, CAMBOX 35KB, PRINT 33KB). After truncation the AI only saw the command intro, param tables/examples all lost, writing params from memory → compile errors.
- **microCompact never empties lookup_command results** — Previously kept the latest 5 tool_results, but complex programs use 10+ commands; beyond 5 the syntax was emptied to `[Old tool result content cleared]`. The AI couldn't find precise syntax when writing code, and was often forced to repeat-query wasting API. Now lookup_command's tool_result is permanently retained; only one-shot results like Read-large-file / WebFetch are emptied.

## [0.1.6] — 2026-06-10

safe-coding force-embed version.

### Fixed

- **AI writing TrioBASIC code didn't follow the safe-coding spec** — The markdown skill previously only listed name + description in the system prompt with no MANDATORY trigger; the AI wouldn't proactively `read_skill('safe-coding')`, writing hard from training memory. Even if it read it one turn, microCompact would empty it 5 turns later. Now `BuildSkillsCatalog` directly embeds the full safe-coding text into the system prompt (~200 tokens), visible every turn, never emptied.

## [0.1.5] — 2026-06-10

Token optimization + IEC stability version.

### Added

- **microCompact tool-result lifecycle management** — Old tool_result content auto-emptied (keeping tool_use_id so pairing isn't broken), retaining the latest 5 full contents. Estimated 30-60% request-token savings.
- **Token-estimate-triggered trimming** — `TrimHistory` uses chars/4 estimation (30k threshold) + count fallback, instead of purely by message count. 30 pure-conversation messages are only 5k tokens, but 5 lookup_commands are 20k+.
- **HTML reference library (IEC/PLCopen)** — The `lookup_command` tool covers all IEC 61131-3 and PLCopen commands/function blocks; the AI actively validates before writing code.
- **Prompt-cache markers** — system prompt + tools + last assistant message stamped with `cache_control` (GLM uses implicit caching, the marker is harmless; switching to an Anthropic endpoint activates it).
- **Smart truncation** — Cuts at HTML heading/table boundaries, avoiding syntax tables being cut mid-sentence.

### Fixed

- **IEC ST local-variable write silently failed** — The LLM output LF line endings, but MP's `STCodeGenerator.SplitCode` internally matches `"VAR\r\n"`, requiring CRLF. Normalized at the `WriteIecSource` / `WriteIecVariables` entry.
- **Newly-created IEC POU not shown in the project tree** — `AddNewProgram`'s folder param can't be null, or `IECObjectPOU`'s constructor `Folder?.Add(this)` skips registration. Changed to pass `EnsureDefaultProgramFolder(false, false)`.
- **IEC auto-create POU always appended to the first existing POU** — `EnsureIecPou` returned `TryGetFirstIecPou` regardless of whether pouName was passed. Changed to match by pouName, create only if not found.

### Adjusted

- `MaxToolResultLen` 16000 → 8000 (~2000 token cap, 50% per-entry savings).
- `BuildSkillsCatalog` drops the 5-command-per-library examples, lists only library name + entry count.
- `BuildProjectContext` program list changed to count + type distribution, no longer listing each name.
- `max_tokens` auto-upgrade 8K → 64K (handle large-program generation).

## [0.1.2] — 2026-06-10

Controller deep-integration + IEC end-to-end support version.

### Added

- **27 HTTP routes / AI tools**, covering deep controller operations:
  - Axis status/detail (`isActive` / `isInError` / `AxisStatus` / `DriveStatus`)
  - System-variable read/write (`/sysvars`, `/sysvar/{name}`)
  - Digital/analog IO read/write (`/io/digital`, `/io/analogue`)
  - Process list + running-variable read (`/processes`, `/processes/{pid}/variables`)
  - Controller event subscription (`/events`)
  - Drive params (`/drive/{axis}/{addr}`)
  - EtherCAT device scan + SDO read/write (`/ethercat/devices`, `/ethercat/sdo`)
  - MS Bus module scan (`/msbus/scan`)
  - Remote device / robot / recipe / alarm lists
  - Oscilloscope open (`/oscilloscope/open`)
  - Project-item list / open project (`/project/items`, `/project/open`)
  - Plugin probe (`/plugins`)
  - Program copy (`/programs/{name}/copy`)

### Fixed

- **IEC program full integration**: `compile` / `run` / `stop` / `upload` / `open` / `breakpoints` list all implemented via reflection on `ContainerTask` public methods. `run` internally auto-Compiles + DebugManager.Start.
- **IEC ST compile error "VAR: missing new statement"**: root cause was the `STCodeGenerator` class's actual namespace being `Trio.PlugIns.IEC61131_3.Models` (not `CodeGenerators`); `asm.GetType()` returned null, so `SplitCode` silently no-op'd, the VAR...END_VAR block was written into the .src file triggering a syntax error. Changed to `asm.GetTypes().FirstOrDefault(t => t.Name == "STCodeGenerator")` to look up by class name.
- **IEC program copy**: switched to `CreateAndAddItem` + source-copy, bypassing `proj.CopyItem`'s file-path ops that don't apply to IEC containers.
- **search_code route**: added to `ApiServer.cs` (previously entirely missing, returned 404).
- **Empty-task IEC source read**: returns empty string instead of throwing "IEC item has no POU".
- **11 new routes' segments.Length off-by-one bug** (io, plugins, robots, recipes, alarms, remote-devices, msbus, ethercat, drive, processes, oscilloscope all affected).

### Known limitations

- IEC breakpoint line→CodeElement reverse inference not implemented; `POST /programs/{name}/breakpoint` returns an explicit error for IEC (line→CodeElement inference needs IEC-parser integration). Use the MP UI to set IEC breakpoints.
- IEC `MAIN`-type POU doesn't support `VAR_INPUT` / `VAR_OUTPUT` (semantic limitation; use SubProgram or UDFB type).

## [0.1.1] — 2026-06-09

Bug-fix version.

### Fixed

- **TrimHistory boundary bug**: When a single user input triggers multiple consecutive tool calls, the recent window may have no plain-text user message (all assistant/tool_use/tool_result pairs). Old logic let the search loop run to the list end, keeping only the last message (usually `user(tool_result)`), orphaning the corresponding `tool_use` → API BadRequest: `tool_use_id found in tool_result blocks`. New logic **skips this trim** when no suitable cut point is found, retaining all history — a temporary token overrun is far easier to handle than a BadRequest.

### Docs

- README adds Zhipu GLM (`GLM-5.1`, `GLM-5`) Anthropic-compatible endpoint config
- README adds the "Skill data initialization (read before first use)" subsection
- README adds the "Why MCP-style instead of real MCP" project-evolution note
- Top adds 8 badges (license / version / .NET / platform / MotionPerfect / API format / Release / stars)



## [0.1] — 2026-06-08

First public release.

### Added

- **AI assistant panel**: MotionPerfect-embedded conversational AI assistant, native WPF UI
- **24 AI tools**:
  - Program management: `list_programs` `create_program` `delete_program` `read_source` `write_source` `patch_source` `open_program` `search_code`
  - Compile & run: `compile_program` `run_program` `stop_program` `upload` `download`
  - Process settings: `get_program_process` `set_program_process`
  - Controller data: `get_status` `list_axes` `read_vr` `write_vr` `read_table` `write_table`
  - Project: `save_project`
  - Knowledge base: `lookup_command` `list_descriptors`
- **HTTP API server** (`http://localhost:9090`): 25 REST endpoints, callable by external Python/Node.js/curl scripts
- **Anthropic Messages API compatible**: supports DeepSeek, official Anthropic, any compatible proxy
- **Streaming responses**: real-time display of AI thinking and tool-call process
- **Two-step confirmation**: write-program, write-VR, run/stop etc. 9 categories of destructive operations require the user to click "Allow"
- **Auto-backup**: All write-program operations auto-backup the original file to `%APPDATA%\TrioAI\backup\` beforehand
- **TrioBASIC strict-syntax constraints**:
  - LOCK-class command interception (prevent controller lockup)
  - Forbid hallucinated TrioBASIC commands (verify via `lookup_command` before writing)
  - Forbid other BASIC-dialect syntax (VB, VB.NET, QBasic, etc.)
  - Forbid variable names conflicting with system reserved names (case-insensitive)
- **Multi-language UI**: Chinese, English, German, French
- **Skills command library**: built-in 918KB TrioBASIC official command reference, index loaded on demand
- **Config UI**: API Key / URL / Model settings, file at `%APPDATA%\TrioAI\config.json`
- **History trimming**: auto-trim over 30 messages, avoid token overrun
- **Pack script** (`pack.py`): one-click `.MPPlugin` package generation

### Security

- LOCK-class command hard-block
- AI system-prompt enforced safety rules
- Destructive-operation UI two-step confirmation
- Write-operation auto-backup

[Unreleased]: https://github.com/lfmmd/TrioAI/compare/v0.1.2...HEAD
[0.1.2]: https://github.com/lfmmd/TrioAI/releases/tag/v0.1.2
[0.1.1]: https://github.com/lfmmd/TrioAI/releases/tag/v0.1.1
[0.1]: https://github.com/lfmmd/TrioAI/releases/tag/v0.1
