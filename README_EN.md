# TrioAI — MotionPerfect AI Assistant Plugin

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Version](https://img.shields.io/badge/version-0.1-blue.svg)](https://github.com/lfmmd/TrioAI/releases)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-blue.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg)](https://www.microsoft.com/windows)
[![MotionPerfect](https://img.shields.io/badge/MotionPerfect-V5.6%20%2F%20V5.7-orange.svg)](https://www.triomotion.com/)
[![API: Anthropic Messages](https://img.shields.io/badge/API-Anthropic%20Messages%20%2Fv1%2Fmessages-ff6b6b.svg)](https://docs.anthropic.com/en/api/messages)
[![GitHub Release](https://img.shields.io/github/v/release/lfmmd/TrioAI?include_prereleases)](https://github.com/lfmmd/TrioAI/releases)
[![GitHub stars](https://img.shields.io/github/stars/lfmmd/TrioAI?style=social)](https://github.com/lfmmd/TrioAI/stargazers)

An AI programming assistant plugin for the Trio MotionPerfect motion controller IDE. AI directly reads/writes programs, interacts with the controller, and queries TrioBASIC command references via tool calls — enabling natural-language-driven motion-control development.

## Features

- **Embedded AI assistant panel** — native WPF UI inside MotionPerfect, no external dependencies
- **24 AI tools** — covering program read/write, controller interaction, VR/TABLE data, TrioBASIC lookup, etc.
- **HTTP API server (MCP-style integration endpoint)** — built-in `localhost:9090` HTTP API, providing [MCP (Model Context Protocol)](https://modelcontextprotocol.io)-like tool access — lets external AI apps, automation scripts, and other LLM agents (Claude Desktop, Cursor, custom clients) invoke MotionPerfect's full functionality (program read/write, controller interaction, VR/TABLE data, etc.) via standard HTTP
- **API format**: [Anthropic Messages API](https://docs.anthropic.com/en/api/messages) (endpoint `/v1/messages`; request body, response, and SSE streaming events all follow the Anthropic spec). **NOT compatible with OpenAI Chat Completions format**. Works with Zhipu GLM (`GLM-5.2`, `GLM-5.1`, `GLM-5`), DeepSeek, Anthropic official, and any Anthropic-Messages-API-compatible proxy or third-party service
- **Streaming responses** — real-time view of AI reasoning and tool execution
- **Two-step confirmation** — all destructive operations (writes, deletes, run, stop) require explicit user approval in the UI
- **Strict TrioBASIC syntax enforcement** — built-in command reference, AI verifies syntax before writing code to prevent hallucination
- **Multilingual UI** — English, Chinese, German, French

## Installation

### Option 1: Install via .MPPlugin package

1. Download `TrioAI.MPPlugin`
2. In MotionPerfect: Tools → Plugin Manager → Install Plugin → select `TrioAI.MPPlugin`
3. Restart MotionPerfect

### Option 2: Manual deployment

1. Extract `TrioAI.MPPlugin` (a ZIP archive) into MotionPerfect's plugin directory:
   ```
   %LOCALAPPDATA%\TrioMotion\MotionPerfectV5.7\Plugins\TrioAI\
   ```
   Expected structure after extraction:
   ```
   TrioAI\
     ├─ TrioAI.MPPlugIn.dll
     └─ skills\triobasic\
          ├─ index.json
          └─ skills.json
   ```
2. Restart MotionPerfect

### Configure API Key

After launching MotionPerfect, click the settings icon at the top of the AI assistant panel and enter:

| Field | Description |
|-------|-------------|
| **API Key** | API key (apply on the provider's console) |
| **API URL** | Base URL (no need to append `/v1/messages` manually — code does it automatically) |
| **Model** | Any model name supported by your provider |

#### Compatible API services

| Provider | API URL (base_url) | Sample models | Get API key |
|----------|---------------------|---------------|-------------|
| **Zhipu GLM** | `https://open.bigmodel.cn/api/anthropic` | `GLM-5.2`, `GLM-5.1`, `GLM-5`, `GLM-4.6` | https://bigmodel.cn |
| **DeepSeek** | `https://api.deepseek.com/anthropic` | `deepseek-v4-pro`, `deepseek-v4-flash`, `deepseek-chat`, `deepseek-reasoner` | https://platform.deepseek.com |
| **Anthropic official** | `https://api.anthropic.com` | `claude-sonnet-4-5`, `claude-opus-4-7`, `claude-haiku-4-5` | https://console.anthropic.com |
| Any Anthropic-compatible proxy | Your proxy base URL | Whatever the proxy supports | Proxy provider |

**Why Anthropic format, not OpenAI?**

Anthropic Messages API has first-class support for **tool use** + **streaming SSE** + **thinking**, all of which are essential for the tool-calling agentic loop needed to control a motion controller. OpenAI's `chat/completions` tool-calling spec also supports tools, but the event structure and parameter conventions differ — this project does not implement it.

If you only have an OpenAI-compatible API, consider using a proxy like [LiteLLM](https://github.com/BerriAI/litellm) to translate to Anthropic format before connecting.

Config file location: `%APPDATA%\TrioAI\config.json`

### Skill data initialization (read this on first use)

TrioAI ships with a ~1 MB TrioBASIC command reference. The AI uses the `lookup_command` tool to verify command syntax in real time and avoid hallucination. **This data must be initialized manually before first use:**

1. Open the AI assistant panel → click the settings icon at the top
2. Find the **"Initialize Skill Data"** button (only visible when not yet initialized; orange)
3. Click → wait a few seconds; a "Initialization complete" dialog appears
4. The button auto-hides

**What initialization does:**
- Copies `skills\triobasic\` (`index.json` + `skills.json`, ~1 MB) from the plugin directory to `%APPDATA%\TrioAI\skills\`
- Deploys `AI_INSTRUCTIONS.md` (created on first run; user edits preserved afterward)
- Sets `skillsInitialized=true` in config.json

**What happens without initialization:**
- AI calls to `lookup_command` get no reference → may hallucinate non-existent TrioBASIC commands
- No crash, but code quality drops significantly

**Re-initialize (restore defaults):**
- Click "Initialize Skill Data" again in the settings panel (visible until initialized)
- Or delete `%APPDATA%\TrioAI\skills\` and restart MP

**After upgrading the .MPPlugin to a new version:** the plugin auto-overwrites local skill data with the new `skills/` (preserves your `AI_INSTRUCTIONS.md` customizations).

## Usage

### AI chat

After opening MotionPerfect, find the **AI Assistant** panel on the left or via the Tools menu, then type your request.

**Sample prompts:**

- *"Write a program that loops checking VR(512) and prints 'hello' when it becomes 1"*
- *"List all programs in the current project"*
- *"Read the source of MAIN1 and explain what it does"*
- *"Compare the syntax difference between MOVE and MOVECIRC"*
- *"Compile and run the AXIS_TEST program"*

### Tool confirmation

When AI wants to execute a destructive operation (write program, write VR, run/stop program, delete program, etc.), a confirmation panel appears at the bottom:

- **Allow** (left, green): execute the operation
- **Reject** (left, red): cancel the operation

Buttons are on the left side of the panel, far from the bottom-right "Send/Stop" button, preventing accidental clicks.

### Input box shortcuts

- **Enter**: send message
- **Shift + Enter**: insert newline

## AI Tools

> 24 tools in total. See [API.md](API.md) for full reference.

**Program management**
- `list_programs` — list all programs in the project
- `create_program` — create a program (requires confirmation)
- `delete_program` — delete a program (requires confirmation)
- `read_source` — read program source
- `write_source` — write full source (requires confirmation)
- `patch_source` — apply line-level edits (requires confirmation)
- `open_program` — open a program in the editor
- `search_code` — search text across all programs

**Compile & run**
- `compile_program` — compile a program
- `run_program` — run a program (requires confirmation)
- `stop_program` — stop a program (requires confirmation)
- `upload` — upload a program to the controller (requires confirmation)
- `download` — download a program from the controller
- `get_program_process` / `set_program_process` — process/autorun settings

**Controller data**
- `get_status` — controller connection status, product name, firmware version
- `list_axes` — list all configured axes
- `read_vr` / `write_vr` — read/write VR variables
- `read_table` / `write_table` — read/write TABLE data

**Project**
- `save_project` — save the current project

**Knowledge base**
- `lookup_command` — query TrioBASIC command/syntax reference
- `list_descriptors` — list available program type descriptors

## HTTP API

The plugin embeds an HTTP server listening on `http://localhost:9090`. Callable from external Python, Node.js, curl scripts for automation.

### Why "MCP-style" rather than true MCP?

Project evolution:

1. **Initial direction**: build an HTTP interface for "external AI tools to operate MotionPerfect via curl" — following the philosophy of [MCP (Model Context Protocol)](https://modelcontextprotocol.io): expose IDE capabilities as a standard interface so any AI client can connect
2. **Realized problem**: this workflow forces users to switch between an external AI assistant (Claude Desktop, Cursor, Claude Code, etc.) and MotionPerfect — the workflow is split. And if you go this route, users might as well use a mature agent like Claude Code directly; wrapping it again adds little value
3. **Refactor**: embed the AI assistant directly inside MotionPerfect — all tools called from one chat panel, seamlessly integrated within the programming IDE

**The current HTTP API is a "legacy" of the first-version architecture.** Technically it's not a strict MCP implementation (MCP uses JSON-RPC; we use REST), but it retains the "let external scripts/agents call the IDE via a standard interface" capability:

- ✅ Still suitable for automation testing, CI integration, batch scripts
- ✅ Still works for external AI clients (not MCP protocol, but the interface is stable and usable)
- ❌ Not directly pluggable into MCP-native apps like Claude Desktop (would require an MCP→HTTP adapter layer)

### Quick examples

```bash
# Get controller status
curl http://localhost:9090/api/status

# List all programs
curl http://localhost:9090/api/programs

# Read MAIN1 source
curl http://localhost:9090/api/programs/MAIN1/source

# Read 10 VR values starting at address 0
curl "http://localhost:9090/api/vr/0?count=10"

# Write VR(0) = 100
curl -X PUT http://localhost:9090/api/vr/0 \
     -H "Content-Type: application/json" \
     -d '{"value": 100}'

# Compile MAIN1
curl -X POST http://localhost:9090/api/programs/MAIN1/compile
```

> Full endpoint list in [API.md](API.md).

## Safety

### AI behavior constraints

The AI system prompt enforces:

1. **NEVER generate LOCK-class commands** (LOCK, LOCK AXIS, LOCK ALL) — they brick the controller
2. **NEVER disable axis drives, brakes, or safety mechanisms**
3. **NEVER hallucinate TrioBASIC syntax** — every command/keyword must be verifiable via `lookup_command`
4. **NEVER use other BASIC dialects** (VB, VB.NET, QBasic, FreeBASIC, etc.) syntax

### Two-step confirmation

These operations require explicit "Allow" click in the UI:

- `write_source` / `patch_source` — write code
- `write_vr` / `write_table` — write controller data
- `create_program` / `delete_program` — create/delete programs
- `run_program` / `stop_program` — motion control
- `upload` — upload to controller
- `set_program_process` — change process settings

### Auto-backup

All program-write operations (`write_source` / `patch_source`) auto-backup the original file to `%APPDATA%\TrioAI\backup\` before overwriting.

## Project structure

```
TrioAI\
  ├─ TrioAIPlugIn.cs        # MP plugin entry (OnInitialize / OnDispose)
  ├─ AiService.cs           # AI call loop, tool definitions, system prompt
  ├─ Handlers.cs            # Implementation of 24 tools
  ├─ ApiServer.cs           # HTTP API server
  ├─ ChatPanel.cs           # WPF UI (chat panel, settings panel, i18n)
  ├─ DispatcherHelper.cs    # UI thread helpers
  ├─ skills\triobasic\      # TrioBASIC command reference data
  │    ├─ index.json        # lazy-loaded index (128KB)
  │    └─ skills.json       # full command library (918KB)
  └─ TrioAI.MPPlugIn.csproj # .NET Framework 4.8 class library
```

## Customizing the AI prompt

The system prompt used by the AI assistant is defined as a string constant `DefaultPrompt` in `AiService.cs`.

**Runtime location:** `%APPDATA%\TrioAI\AI_INSTRUCTIONS.md`

**Two ways to customize (pick one):**

1. **Edit the file directly** (recommended for personal tweaks)
   - If the file doesn't exist, the plugin creates it from the in-code `DefaultPrompt` on first launch
   - After that, your edits persist across MP restarts — the file is NOT overwritten
   - To restore the default: delete the file; it will be regenerated on next MP launch

2. **Modify source code** (recommended for forks/releases)
   - Edit `DefaultPrompt` in `AiService.cs`
   - Rebuild with `dotnet build -c Release`
   - Existing users must delete `%APPDATA%\TrioAI\AI_INSTRUCTIONS.md` to receive the new prompt
   - Or click the "Initialize Skills" button in the UI to force a reset

## Build

Requires:
- .NET Framework 4.8 SDK
- MotionPerfect installed (depends on `TrioSharedLibrary.dll`, `TrioCommunicationsLibrary.dll`, `TrioBaseLib.dll` — place them in the parent directory of the project)

```bash
cd TrioAI
dotnet build -c Release
# Output: bin\Release\TrioAI.MPPlugIn.dll
```

Package as `.MPPlugin`:

```bash
python pack.py
# Output: TrioAI.MPPlugin (ZIP format)
```

## Compatibility

- MotionPerfect V5.6 / V5.7
- .NET Framework 4.8
- Windows 10 / 11

## License

[MIT License](LICENSE)
