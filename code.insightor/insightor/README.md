# Insightor – VS Code Extension

C# algorithm helper that surfaces live variable values as inlay hints while you run instrumented code. Includes rich timeline, variables table, call graph, and an animator for stepping through execution.

## Features

- Inlay hints appended to each line showing current variable values and return values
- Session-based runs: edits re-run automatically against a temp buffer
- Timeline panel with draggable range selection and annotated bars per call
- Variables Table panel that pivots variables for selected lines across steps
- Call Graph panel that builds a hierarchical graph with argument and return annotations
- Animator panel to play, pause, step next/prev, and adjust speed; highlights active line

## Commands

- `insightor.startSession` – Insightor: Start Session
- `insightor.restartSession` – Insightor: Restart Session
- `insightor.stopSession` – Insightor: Stop Session
- `insightor.showTimeline` – Insightor: Show Timeline
- `insightor.showVariablesTable` – Insightor: Show Variables Table
- `insightor.showCallGraph` – Insightor: Show Call Graph
- `insightor.showAnimator` – Insightor: Show Animator

## Requirements

- .NET SDK 9 (used by the CLI instrumenter)
- VS Code 1.104+

## Getting Started

1. Open a C# file
2. Run “Insightor: Start Session”
3. Use the timeline to adjust the visible event range
4. Open Variables Table / Call Graph / Animator as needed

## Inlay Format

- Per probe: `a: 1, b: 2`
- Multiple probes per line: `a: 1 | b: 2, return: 3`

## What's New

- Added Variables Table view
- Added Call Graph view with argument and return annotations
- Added Animator with play/pause/step and speed control
- Timeline hover tooltips now display method arguments

## Known Issues

- Requires a buildable C# project; errors in the source may prevent runs

## License

MIT
