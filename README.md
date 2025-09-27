# Insightor

C# algorithm exploration toolkit with a Roslyn-powered CLI instrumenter and a VS Code extension that overlays live variable values as inlay hints. A small sample app is included.

## Apps

- VS Code Extension: `code.insightor/insightor`
- CLI Instrumenter: `cli.insightor/Insightor`
- Sample snippets: `Sample/`

## Features

### CLI (Instrumentation & Probes)

- Roslyn-based rewriting inserts probes to capture runtime values:
  - Local declarations: declared variables plus referenced identifiers in initializers
  - Expression statements
  - Return statements: emits a special `return: {value}` entry via a temp binding
  - If / else-if / else:
    - Condition probe before each if/else-if evaluation
    - Supports single-statement (unbraced) bodies
  - Loops:
    - `for`: header probe per iteration (init/cond/inc references)
    - `while`: condition probe per iteration
  - Lambdas:
    - Expression-bodied lambdas rewritten to block form to emit `return: {value}` (e.g. `.Select(x => x * 2)`)
- Scope-safe identifier collection (doesn’t cross into lambda/local-function parameter scopes)
- Thread-safe `__Probe` logger (JSON Lines output)
- Implicit usings + platform references so simple programs compile directly

### VS Code Extension (Inlays, Sessions, Timeline)

- Inlay hints at end of each line
  - Variables in a probe are comma-separated; multiple probes on a line are joined by `|`
  - Special display for `return: {value}`
  - Left padding for readability
- Session model
  - One active session per file
  - Runs against a persistent temp copy of the buffer (no forced save)
  - First run uses `dotnet run`; subsequent runs reuse compiled `Insightor.dll`
- Timeline panel (vertical)
  - Notches for each probe entry by sequence
  - Draggable start/end handles (live updates while dragging)
  - Click to jump the nearest handle
  - Inlays are filtered to the selected probe range

### Commands (VS Code)

- Insightor: Start Session
- Insightor: Restart Session
- Insightor: Stop Session
- Insightor: Show Timeline
- Insightor: Show Variables Table
- Insightor: Show Call Graph
- Insightor: Show Animator

## What's New

- Call Graph view: visualize nested method calls over the selected range with argument and return annotations; click nodes in the timeline to filter.
- Variables Table: pivot variables for selected lines into a table across probe steps.
- Animator: play, pause, step through probes; highlights current line and filters inlays to the active step.
- Timeline improvements: draggable range handles, richer hover tooltips with method arguments, clearer labeling.
- CLI probe enhancements: parameter bindings included in call start/end events; improved return value handling.

## Architecture

```text
+---------------------------+            +-----------------------+
| VS Code Extension         |            | CLI (dotnet / Roslyn) |
|  - Session Manager        |  dotnet    |  - Program.cs         |
|  - InlayHintsProvider     +----------->|    - Roslyn rewriter  |
|  - Timeline Webview       |            |    - __Probe logger   |
|  - Dotnet Runner          |  JSONL     |  - Emits JSONL lines  |
|  - Buffer->Temp writer    <-----------+|                       |
+---------------------------+            +-----------------------+
```

- Extension writes the active buffer to a temp `.cs`, invokes the CLI, parses JSONL, and renders inlays.
- Timeline webview posts range updates to the extension; inlays are filtered by probe sequence.

## How to Use

### Prerequisites

- .NET SDK 9 (CLI/tests)
- Node.js 18+ (extension build)
- VS Code 1.104+

### Build & Test CLI

```bash
dotnet build .\cli.insightor\Insightor\Insightor.csproj -c Debug

dotnet test .\cli.insightor\Insightor.Tests\Insightor.Tests.csproj -v minimal
```

### Run CLI Manually

```bash
# dotnet run --project <csproj> -- <input.cs> <output.jsonl>

dotnet run --project .\cli.insightor\Insightor\Insightor.csproj -- .\Sample\Program.cs .\.out.jsonl
```

### Build the Extension

```bash
cd code.insightor/insightor
npm install
npm run compile
```

### Launch in VS Code

- Open the repo in VS Code
- Press F5 to start an Extension Development Host

### Workflow

1. Open a C# file
2. Run “Insightor: Start Session”
   - Extension writes a temp copy and runs the CLI; inlays appear
3. Edit/Save – session re-runs automatically (debounced on edits)
4. “Insightor: Show Timeline” to filter visible inlays via start/end handles
5. “Restart Session” to reset; “Stop Session” to stop

### Inlay Format

- Per probe: `a: 1, b: 2`
- Multiple probes on a line: `a: 1 | b: 2, return: 3`

## Contributors (last 180 days)

- passelin

## Repository Layout

```text
Insightor/
├─ README.md
├─ cli.insightor/
│  └─ Insightor/
│     ├─ Insightor.csproj
│     ├─ Program.cs
│     └─ Insightor.Tests/
│        ├─ *.cs
│        └─ Insightor.Tests.csproj
├─ code.insightor/
│  └─ insightor/
│     ├─ src/extension.ts
│     ├─ package.json
│     └─ webpack.config.js
└─ Sample/
   └─ Program.cs
```

## Troubleshooting

- Intermittent Windows build locks: re-run tests; extension uses `--no-build` after first build.
- If inlays don’t show: confirm CLI build succeeded and a session is running for the active file.
