import * as vscode from 'vscode';
import { state } from '../core/state';
import { getFilteredLineEntries } from './execution';
import type { AnyEvent, Frame, ExtendedProbeEntry } from '../core/types';

/**
 * Builds frame data structure from events for timeline visualization
 * @param events Array of events to process
 * @returns Array of frames representing method call hierarchy
 */
export function buildFrames(events: AnyEvent[]): Frame[] {
  const frames: Frame[] = [];
  const stack: { method: string; start: number; line?: number; args?: Record<string, unknown> }[] = [];
  let nextId = 1;

  for (const ev of events) {
    const t = ev.type ?? 'line';

    if (t === 'callStart') {
      const method = String(ev.method ?? '');
      stack.push({
        method,
        start: ev.__seq,
        line: ev.line,
        args: (ev.variables ?? {}) as Record<string, unknown>
      });
    } else if (t === 'callEnd') {
      const method = String(ev.method ?? '');
      for (let i = stack.length - 1; i >= 0; i--) {
        if (stack[i].method === method) {
          const depth = i;
          const start = stack[i].start;
          const line = stack[i].line;
          frames.push({
            id: nextId++,
            method,
            start,
            end: ev.__seq,
            depth,
            line,
            args: stack[i].args
          });
          stack.splice(i, 1);
          break;
        }
      }
    }
  }

  // Close any unclosed frames at end
  const lastSeq = events.length > 0 ? events[events.length - 1].__seq : 0;
  for (let i = 0; i < stack.length; i++) {
    const f = stack[i];
    frames.push({
      id: nextId++,
      method: f.method,
      start: f.start,
      end: lastSeq,
      depth: i,
      line: f.line,
      args: f.args
    });
  }

  return frames;
}

/**
 * Updates the timeline webview with new frame data
 * @param events Events to display in the timeline
 */
export function updateTimelineWebview(events: AnyEvent[]): void {
  if (!state.timelinePanel) return;

  const total = Math.max(0, (state.lastResults.get(state.session?.doc.uri.toString() ?? '') ?? []).length);
  const range = state.session?.range ?? { start: 0, end: Math.max(0, total - 1) };
  const frames = buildFrames(events);

  state.timelinePanel.webview.postMessage({
    type: 'frames',
    total,
    range,
    frames
  });
}

/**
 * Pushes variables data to the variables table webview
 */
export function pushVariablesData(): void {
  if (!state.variablesPanel || !state.session) return;

  const doc = state.session.doc;
  const entries = getFilteredLineEntries(doc);
  const byLine = new Map<number, ExtendedProbeEntry[]>();

  for (const e of entries) {
    const idx = e.line - 1;
    if (!byLine.has(idx)) byLine.set(idx, []);
    byLine.get(idx)!.push(e);
  }

  const selections = vscode.window.activeTextEditor?.selections ?? [];
  const selectedLines = new Set<number>();

  for (const sel of selections) {
    for (let i = sel.start.line; i <= sel.end.line; i++) {
      selectedLines.add(i);
    }
  }

  // Active lines are those with inlays visible in current range and also selected lines
  const activeSelectedLines = Array.from(selectedLines)
    .filter(l => byLine.has(l))
    .sort((a, b) => a - b);

  const tables = activeSelectedLines.map(lineIdx => buildTableModel(lineIdx, byLine.get(lineIdx)!));

  state.variablesPanel.webview.postMessage({
    type: 'tables',
    tables
  });
}

/**
 * Builds a table model for the variables table display
 * @param lineIdx Line index (0-based)
 * @param probes Probe entries for this line
 * @returns Table model with columns and rows
 */
export function buildTableModel(lineIdx: number, probes: ExtendedProbeEntry[]) {
  // Columns: iteration index (seq); Rows: union of variable names across probes
  const varSet = new Set<string>();
  for (const p of probes) {
    for (const k of Object.keys(p.variables)) {
      varSet.add(k);
    }
  }

  const varNames = Array.from(varSet).sort();
  const cols = probes.map(p => p.__seq);
  const rows = varNames.map(name => ({
    name,
    values: probes.map(p => formatValue(p.variables[name]))
  }));

  return { line: lineIdx + 1, cols, rows };
}

/**
 * Pushes call graph data to the call graph webview
 */
export function pushCallGraphData(): void {
  if (!state.callGraphPanel || !state.session) return;

  const events = state.lastResults.get(state.session.doc.uri.toString()) ?? [];
  const frames = buildFrames(events);

  // Determine selected time window from range
  const range = state.session.range;

  // Build edges by parent-child from frames nesting within selected range
  const nodes = new Map<number, { id: number; label: string; start: number; end: number }>();
  const edges: { from: number; to: number }[] = [];
  const active = frames.filter(f => f.end >= range.start && f.start <= range.end);

  for (const f of active) {
    nodes.set(f.id, { id: f.id, label: f.method, start: f.start, end: f.end });
  }

  // Parent: the deepest frame that strictly encloses this frame
  for (const child of active) {
    let parent: typeof child | null = null;
    for (const cand of active) {
      if (cand.id === child.id) continue;
      if (cand.start <= child.start && cand.end >= child.end && cand.depth < child.depth) {
        if (!parent || cand.depth > parent.depth) parent = cand;
      }
    }
    if (parent) edges.push({ from: parent.id, to: child.id });
  }

  // Attach argument/return data: search events between start/end
  const annotations: Record<number, { args: Record<string, unknown>; ret?: unknown }> = {};
  for (const f of active) {
    const evs = events.filter(e => e.__seq >= f.start && e.__seq <= f.end);
    const startEv = evs.find(e => (e.type ?? 'line') === 'callStart' && (e.method || '') === f.method);
    const retLineEv = [...evs].reverse().find(e =>
      (e.type ?? 'line') === 'line' &&
      e.variables &&
      Object.prototype.hasOwnProperty.call(e.variables, 'return')
    );

    annotations[f.id] = {
      args: (startEv?.variables as any) || {},
      ret: retLineEv?.variables ? (retLineEv.variables as any)['return'] : undefined
    };
  }

  state.callGraphPanel.webview.postMessage({
    type: 'graph',
    nodes: Array.from(nodes.values()),
    edges,
    annotations
  });
}

/**
 * Formats a value for display in the UI
 * @param value Value to format
 * @returns Formatted string representation
 */
export function formatValue(value: unknown): string {
  if (value === null || value === undefined) return 'null';
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}
