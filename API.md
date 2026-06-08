# TrioAI API Reference

This document describes the two interfaces TrioAI exposes:

1. **HTTP API** — REST endpoints on `http://localhost:9090` for external automation
2. **AI Tools** — tool calls the AI assistant can make inside MotionPerfect

All endpoints return JSON. All `PUT`/`POST` bodies are JSON (`Content-Type: application/json`).

---

## HTTP API

Base URL: `http://localhost:9090`

CORS is enabled (`Access-Control-Allow-Origin: *`), so browsers can call directly.

### Status & Project

| Method | Path | Description | Body |
|--------|------|-------------|------|
| `GET` | `/api/status` | Controller connection status, product name, firmware version, project name | — |
| `POST` | `/api/project` | Create a new project | — |
| `PUT` | `/api/project` | Save the current project | — |

### Programs

| Method | Path | Description | Body |
|--------|------|-------------|------|
| `GET` | `/api/programs` | List all programs in the project | — |
| `POST` | `/api/programs` | Create a program | `{name, type?, sourceCode?}` |
| `GET` | `/api/programs/{name}` | (alias of `/source` for some metadata) | — |
| `DELETE` | `/api/programs/{name}` | Delete a program | — |
| `PUT` | `/api/programs/{name}/rename` | Rename a program | `{newName}` |
| `GET` | `/api/programs/{name}/source` | Read program source | — |
| `PUT` | `/api/programs/{name}/source` | Overwrite program source (auto-backup) | `{sourceCode}` |
| `PATCH` | `/api/programs/{name}/source` | Apply line-level edits (auto-backup) | `{operations: [{action, line, content}, ...]}` |
| `POST` | `/api/programs/{name}/open` | Open the program in the editor | — |
| `POST` | `/api/programs/{name}/compile` | Compile the program | — |
| `POST` | `/api/programs/{name}/upload` | Upload to the controller | — |
| `POST` | `/api/programs/{name}/download` | Download from the controller | — |
| `POST` | `/api/programs/{name}/run` | Run the program | `{process?}` |
| `POST` | `/api/programs/{name}/stop` | Stop the program | `{process?}` |
| `GET` | `/api/programs/{name}/process` | Get process/autorun settings (isAutorun, autorunProcess, processAffinity) | — |
| `PUT` | `/api/programs/{name}/process` | Set process/autorun settings | `{isAutorun?, autorunProcess?, processAffinity?}` |

### Controller Data

| Method | Path | Description | Body / Query |
|--------|------|-------------|--------------|
| `GET` | `/api/vr/{address}?count=N` | Read N VR values starting at address (default N=1) | — |
| `PUT` | `/api/vr/{address}` | Write a value to a VR variable | `{value}` |
| `GET` | `/api/table/{address}?count=N` | Read N TABLE values starting at address (default N=1) | — |
| `PUT` | `/api/table/{address}` | Write values to TABLE | `{values: [...]}` |
| `GET` | `/api/axes` | List all configured axes | — |

### Misc

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/descriptors` | List available program type descriptors |
| `POST` | `/api/chat` | Open the AI chat panel in MotionPerfect |

### Common error responses

| Status | When |
|--------|------|
| `400 Bad Request` | Invalid path parameter (e.g. non-numeric VR address) |
| `404 Not Found` | Unknown route or program not found |
| `405 Method Not Allowed` | Wrong HTTP method for the route |
| `500 Internal Server Error` | Handler threw an exception (error message in body) |

### Examples

```bash
# Get status
curl http://localhost:9090/api/status
# → {"connected": true, "productName": "MC4N", "firmware": "2.16b4", "project": "demo"}

# List programs
curl http://localhost:9090/api/programs
# → {"programs": ["MAIN1", "AXIS_TEST", "VR_DEMO"]}

# Read MAIN1 source
curl http://localhost:9090/api/programs/MAIN1/source
# → {"sourceCode": "' MAIN1 - ...\nPRINT \"hello\"\n"}

# Patch lines 5 and 10
curl -X PATCH http://localhost:9090/api/programs/MAIN1/source \
     -H "Content-Type: application/json" \
     -d '{"operations":[
           {"action":"replace","line":5,"content":"PRINT \"new line 5\""},
           {"action":"insert","line":10,"content":"' inserted comment"}
         ]}'

# Read VR(0..9)
curl "http://localhost:9090/api/vr/0?count=10"
# → {"address": 0, "values": [0, 0, 100, 0, 0, 0, 0, 0, 0, 0]}

# Write VR(0) = 100
curl -X PUT http://localhost:9090/api/vr/0 \
     -H "Content-Type: application/json" \
     -d '{"value": 100}'

# Compile MAIN1
curl -X POST http://localhost:9090/api/programs/MAIN1/compile
# → {"success": true, "errors": 0, "warnings": 0}

# Run MAIN1 in process 1
curl -X POST http://localhost:9090/api/programs/MAIN1/run \
     -H "Content-Type: application/json" \
     -d '{"process": 1}'
```

---

## AI Tools

These are the tools exposed to the AI model via Anthropic-style `tool_use`. The AI decides when to call them based on user intent. Tools marked **[confirm]** require user approval in the UI before execution.

### Status & discovery

#### `get_status`
**Description:** Get controller connection status, product name, firmware version, project name.
**Parameters:** none

#### `list_programs`
**Description:** List all programs in the current MotionPerfect project.
**Parameters:** none

#### `list_axes`
**Description:** List all configured axes on the controller.
**Parameters:** none

#### `list_descriptors`
**Description:** List available program type descriptors.
**Parameters:** none

### Program source

#### `read_source`
**Description:** Read source code of a program. Auto-paginates for large files (>200 lines or >8000 chars) by returning the first chunk + `totalLines` + a `hint` telling you the next `startLine` to use.
**Parameters:**
- `name` (string, required) — Program name
- `startLine` (integer, optional) — 1-based starting line for pagination
- `endLine` (integer, optional) — Ending line for pagination

**Returns (full):**
```json
{"sourceCode": "...", "totalLines": 50}
```

**Returns (large file, auto-paginated):**
```json
{
  "sourceCode": "first 200 lines or 8000 chars...",
  "startLine": 1,
  "endLine": 200,
  "totalLines": 500,
  "truncated": true,
  "hint": "Large file (500 lines, 15000 chars). To read the next chunk, call read_source with startLine=201."
}
```

#### `write_source` [confirm]
**Parameters:**
- `name` (string, required) — Program name
- `sourceCode` (string, required) — Full source to overwrite

#### `patch_source` [confirm]
**Parameters:**
- `name` (string, required) — Program name
- `operations` (array, required) — Each item:
  ```json
  {
    "action": "replace | insert | delete",
    "line": <1-based line number>,
    "content": "<new line text>"
  }
  ```
  - `replace`: overwrite the line at `line`
  - `insert`: insert a new line **before** `line` (or at end if `line > total`)
  - `delete`: remove the line at `line`

#### `open_program`
**Parameters:**
- `name` (string, required) — Program name

#### `search_code`
**Description:** Search text/pattern across all programs. Returns matching lines with line numbers.
**Parameters:**
- `query` (string, required) — Search text
- `caseSensitive` (boolean, optional, default false)

### Program lifecycle

#### `create_program` [confirm]
**Parameters:**
- `name` (string, required)
- `type` (string, optional) — e.g. `basic`, `text`
- `sourceCode` (string, optional) — Initial content

#### `delete_program` [confirm]
**Parameters:**
- `name` (string, required)

#### `compile_program`
**Parameters:**
- `name` (string, required)

#### `run_program` [confirm]
**Parameters:**
- `name` (string, required)
- `process` (integer, optional) — Process slot

#### `stop_program` [confirm]
**Parameters:**
- `name` (string, optional)
- `process` (integer, optional) — Process slot

#### `upload` [confirm]
**Description:** Upload a program from project to the controller.
**Parameters:**
- `name` (string, required)

#### `download`
**Description:** Download a program from the controller into the project.
**Parameters:**
- `name` (string, required)

### Process settings

#### `get_program_process`
**Description:** Returns `isAutorun`, `autorunProcess`, `processAffinity`.
**Parameters:**
- `name` (string, required)

#### `set_program_process` [confirm]
**Parameters:**
- `name` (string, required)
- `isAutorun` (boolean, optional) — Auto-run on controller startup
- `autorunProcess` (integer, optional) — Process slot for auto-run
- `processAffinity` (integer, optional) — Process affinity

### Controller data

#### `read_vr`
**Parameters:**
- `address` (integer, required) — Starting VR address (0-based)
- `count` (integer, optional) — Number of values; default 1

#### `write_vr` [confirm]
**Parameters:**
- `address` (integer, required)
- `value` (number, required)

#### `read_table`
**Parameters:**
- `address` (integer, required)
- `count` (integer, optional)

#### `write_table` [confirm]
**Parameters:**
- `address` (integer, required)
- `values` (array of numbers, required)

### Project

#### `save_project`
**Parameters:** none

### Knowledge base

#### `lookup_command`
**Description:** Query TrioBASIC command/keyword reference from the official manual. Use this **before** writing any code that uses an unfamiliar command.
**Parameters:**
- `query` (string, required) — Command name or keyword (e.g. `MOVE`, `CONNECT`, `ACCEL`, `FOR`)

---

## Common patterns

### AI: "read, modify, write"

```
User: "Add a print statement to MAIN1 line 5"

AI calls:
  1. read_source(name="MAIN1")          → see current code
  2. patch_source(name="MAIN1",          → apply edit (user confirms)
        operations=[{action:"replace",
                     line:5,
                     content:"PRINT \"new\""}])
```

### External: automated testing

```python
import requests

base = "http://localhost:9090"

# Compile all programs
progs = requests.get(f"{base}/api/programs").json()["programs"]
for p in progs:
    r = requests.post(f"{base}/api/programs/{p}/compile").json()
    print(f"{p}: {r['errors']} errors, {r['warnings']} warnings")
```

### External: log VR every second

```python
import time, requests

while True:
    val = requests.get("http://localhost:9090/api/vr/512").json()
    print(f"VR(512) = {val['values'][0]}")
    time.sleep(1)
```
