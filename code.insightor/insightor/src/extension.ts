// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as vscode from 'vscode';
import { spawn } from 'child_process';
import * as path from 'path';

type AnyEvent = { type?: 'line' | 'callStart' | 'callEnd'; line?: number; method?: string; variables?: Record<string, unknown>; __seq: number };
type ProbeEntry = { line: number; variables: Record<string, unknown> };
type ExtendedProbeEntry = ProbeEntry & { __seq: number };

const state = {
  lastResults: new Map<string, AnyEvent[]>(),
  session: null as null | {
    doc: vscode.TextDocument,
    disposables: vscode.Disposable[],
    timer: NodeJS.Timeout | null,
    builtDllPath: string | null,
    tempPath: string,
    range: { start: number; end: number },
  },
  timelinePanel: null as null | vscode.WebviewPanel,
};

// This method is called when your extension is activated
// Your extension is activated the very first time the command is executed
export function activate(context: vscode.ExtensionContext) {

	// Use the console to output diagnostic information (console.log) and errors (console.error)
	// This line of code will only be executed once when your extension is activated
	console.log('Congratulations, your extension "insightor" is now active!');

	// The command has been defined in the package.json file
	// Now provide the implementation of the command with registerCommand
	// The commandId parameter must match the command field in package.json
	const inlayProvider = new InsightorInlayProvider();

	const startSession = vscode.commands.registerCommand('insightor.startSession', async () => {
		const editor = vscode.window.activeTextEditor;
		if (!editor) { vscode.window.showWarningMessage('Open a C# file first.'); return; }
		const doc = editor.document;
		if (doc.languageId !== 'csharp' && !doc.fileName.endsWith('.cs')) { vscode.window.showWarningMessage('Not a C# file.'); return; }
		await ensureStorageDir(context);
		stopCurrentSession();
		const tempPath = path.join(context.globalStorageUri.fsPath, 'insightor.session-buffer.cs');
		state.session = { doc, disposables: [], timer: null, builtDllPath: null, tempPath, range: { start: 0, end: Number.MAX_SAFE_INTEGER } };
		// Re-run on edits (debounced)
		const editSub = vscode.workspace.onDidChangeTextDocument(e => {
			if (state.session?.doc.uri.toString() !== e.document.uri.toString()) return;
			debouncedRun(context, inlayProvider);
		});
		const saveSub = vscode.workspace.onDidSaveTextDocument(d => {
			if (state.session?.doc.uri.toString() !== d.uri.toString()) return;
			debouncedRun(context, inlayProvider, 0);
		});
		const closeSub = vscode.workspace.onDidCloseTextDocument(d => {
			if (state.session?.doc.uri.toString() === d.uri.toString()) stopCurrentSession();
		});
		state.session.disposables.push(editSub, saveSub, closeSub);
		await runInsightorNow(context, inlayProvider);
	});

	const restartSession = vscode.commands.registerCommand('insightor.restartSession', async () => {
		const editor = vscode.window.activeTextEditor;
		if (!editor) { vscode.window.showWarningMessage('Open a C# file first.'); return; }
		const doc = editor.document;
		if (doc.languageId !== 'csharp' && !doc.fileName.endsWith('.cs')) { vscode.window.showWarningMessage('Not a C# file.'); return; }
		const builtDllPath = state.session?.builtDllPath ?? null;
    const priorTemp = state.session?.tempPath;
		stopCurrentSession();
		const tempPath = priorTemp ?? path.join(context.globalStorageUri.fsPath, 'insightor.session-buffer.cs');
		state.session = { doc, disposables: [], timer: null, builtDllPath, tempPath, range: { start: 0, end: Number.MAX_SAFE_INTEGER } };
		const editSub = vscode.workspace.onDidChangeTextDocument(e => {
			if (state.session?.doc.uri.toString() !== e.document.uri.toString()) return;
			debouncedRun(context, inlayProvider);
		});
		const saveSub = vscode.workspace.onDidSaveTextDocument(d => {
			if (state.session?.doc.uri.toString() !== d.uri.toString()) return;
			debouncedRun(context, inlayProvider, 0);
		});
		state.session.disposables.push(editSub, saveSub);
		await runInsightorNow(context, inlayProvider);
	});

	const stopSession = vscode.commands.registerCommand('insightor.stopSession', async () => {
		stopCurrentSession();
	});

	const showTimeline = vscode.commands.registerCommand('insightor.showTimeline', async () => {
		openOrRevealTimeline(context, inlayProvider);
	});

	context.subscriptions.push(
		vscode.languages.registerInlayHintsProvider({ language: 'csharp' }, inlayProvider),
		startSession,
		restartSession,
		stopSession,
		showTimeline
	);
}

// This method is called when your extension is deactivated
export function deactivate() {}

class InsightorInlayProvider implements vscode.InlayHintsProvider {
  private _onDidChangeInlayHints = new vscode.EventEmitter<void>();
  onDidChangeInlayHints = this._onDidChangeInlayHints.event;

  refresh(doc?: vscode.TextDocument) {
    this._onDidChangeInlayHints.fire();
  }

  provideInlayHints(document: vscode.TextDocument, range: vscode.Range, token: vscode.CancellationToken): vscode.ProviderResult<vscode.InlayHint[]> {
    const key = document.uri.toString();
    const entries = getFilteredLineEntries(document);
    const hints: vscode.InlayHint[] = [];
    const byLine = new Map<number, ExtendedProbeEntry[]>();
    for (const e of entries) {
      const idx = e.line - 1;
      if (!byLine.has(idx)) byLine.set(idx, []);
      byLine.get(idx)!.push(e);
    }
    for (const [lineIdx, group] of byLine.entries()) {
      if (lineIdx < 0 || lineIdx >= document.lineCount) continue;
      const line = document.lineAt(lineIdx);
      if (!range.contains(line.range)) continue;
      const perProbeTexts = group.map(entry => {
        const varsEntries = Object.entries(entry.variables);
        const retEntry = varsEntries.find(([k]) => k === 'return');
        const nonReturn = varsEntries.filter(([k]) => k !== 'return');
        const parts: string[] = [];
        parts.push(...nonReturn.map(([k, v]) => `${k}: ${formatValue(v)}`));
        if (retEntry) parts.push(`return: ${formatValue(retEntry[1])}`);
        return parts.join(', ');
      }).filter(t => t.length > 0);
      if (perProbeTexts.length === 0) continue;
      const text = perProbeTexts.join(' | ');
      const position = new vscode.Position(lineIdx, line.range.end.character);
      const hint = new vscode.InlayHint(position, text, vscode.InlayHintKind.Type);
      hint.paddingLeft = true;
      hints.push(hint);
    }
    return hints;
  }
}

function formatValue(v: unknown): string {
  if (v === null || v === undefined) return 'null';
  if (typeof v === 'object') return JSON.stringify(v);
  return String(v);
}

function stopCurrentSession() {
  if (!state.session) return;
  if (state.session.timer) { clearTimeout(state.session.timer); }
  for (const d of state.session.disposables) try { d.dispose(); } catch {}
  state.session = null;
}

function debouncedRun(context: vscode.ExtensionContext, provider: InsightorInlayProvider, delay = 350) {
  if (!state.session) return;
  if (state.session.timer) clearTimeout(state.session.timer);
  state.session.timer = setTimeout(() => { runInsightorNow(context, provider).catch(() => {}); }, delay);
}

async function ensureStorageDir(context: vscode.ExtensionContext) {
  const outDir = context.globalStorageUri.fsPath;
  await vscode.workspace.fs.createDirectory(vscode.Uri.file(outDir));
}

async function runInsightorNow(context: vscode.ExtensionContext, provider: InsightorInlayProvider) {
  if (!state.session) return;
  const doc = state.session.doc;
  const outPath = path.join(context.globalStorageUri.fsPath, 'insightor.out.jsonl');
  try { await vscode.workspace.fs.delete(vscode.Uri.file(outPath)); } catch {}

  // Write current buffer to temp file to avoid forcing a save
  const tempPath = state.session.tempPath;
  const buffer = Buffer.from(doc.getText(), 'utf8');
  await vscode.workspace.fs.writeFile(vscode.Uri.file(tempPath), buffer);

  const workspaceFolder = vscode.workspace.getWorkspaceFolder(doc.uri);
  if (!workspaceFolder) { vscode.window.showErrorMessage('Open a workspace to run Insightor.'); return; }
  const extDir = vscode.extensions.getExtension('insightor')?.extensionUri.fsPath ?? context.extensionUri.fsPath;
  const cliProj = path.resolve(extDir, '..', '..', 'cli.insightor', 'Insightor', 'Insightor.csproj');
  const dllPath = path.resolve(extDir, '..', '..', 'cli.insightor', 'Insightor', 'bin', 'Debug', 'net9.0', 'Insightor.dll');

  const status = vscode.window.setStatusBarMessage('$(play) Insightor: running...');
  try {
    if (!state.session.builtDllPath) {
      await execDotnet(['run', '--project', cliProj, '--', tempPath, outPath], path.dirname(cliProj));
      // After first successful run, reuse built dll
      state.session.builtDllPath = dllPath;
    } else {
      await execDotnet([state.session.builtDllPath, tempPath, outPath], path.dirname(cliProj));
    }
    const content = (await vscode.workspace.fs.readFile(vscode.Uri.file(outPath))).toString();
    const events: AnyEvent[] = content.split(/\r?\n/)
      .filter(Boolean)
      .map((l, i) => {
        const raw: any = JSON.parse(l);
        const type = (raw?.type as AnyEvent['type']) ?? 'line';
        if (type === 'line') {
          return { type, line: raw.line as number, variables: (raw.variables ?? {}) as Record<string, unknown>, __seq: i } as AnyEvent;
        }
        if (type === 'callStart' || type === 'callEnd') {
          return { type, method: String(raw.method ?? ''), line: raw.line as number | undefined, __seq: i } as AnyEvent;
        }
        return { type: 'line', line: raw.line as number, variables: (raw.variables ?? {}) as Record<string, unknown>, __seq: i } as AnyEvent;
      });
    state.lastResults.set(doc.uri.toString(), events);
    provider.refresh(doc);
    updateTimelineWebview(events);
  } catch (err: any) {
    vscode.window.showErrorMessage('Insightor failed: ' + (err?.message ?? String(err)));
  } finally {
    status.dispose();
  }
}

function execDotnet(args: string[], cwd: string): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    const proc = spawn('dotnet', args, { cwd });
    let stderr = '';
    proc.stderr.on('data', d => { stderr += d.toString(); });
    proc.on('error', reject);
    proc.on('exit', code => code === 0 ? resolve() : reject(new Error('Exit ' + code + '\n' + stderr)));
  });
}

function getFilteredLineEntries(document: vscode.TextDocument): ExtendedProbeEntry[] {
  const key = document.uri.toString();
  const entries = state.lastResults.get(key) ?? [];
  const range = state.session?.range ?? { start: 0, end: Number.MAX_SAFE_INTEGER };
  const filtered = entries.filter(e => e.__seq >= range.start && e.__seq <= range.end && (e.type ?? 'line') === 'line' && e.line != null && e.variables != null);
  return filtered.map(e => ({ line: e.line as number, variables: e.variables as Record<string, unknown>, __seq: e.__seq }));
}

function openOrRevealTimeline(context: vscode.ExtensionContext, provider: InsightorInlayProvider) {
  if (state.timelinePanel) {
    state.timelinePanel.reveal();
    return;
  }
  const panel = vscode.window.createWebviewPanel(
    'insightorTimeline',
    'Insightor Timeline',
    vscode.ViewColumn.Beside,
    { enableScripts: true, retainContextWhenHidden: true }
  );
  state.timelinePanel = panel;
  panel.onDidDispose(() => { state.timelinePanel = null; });
  panel.webview.html = getTimelineHtml();
  panel.webview.onDidReceiveMessage(msg => {
    if (msg?.type === 'updateRange' && state.session) {
      const total = (state.lastResults.get(state.session.doc.uri.toString()) ?? []).length;
      const clamp = (n: number) => Math.max(0, Math.min(total - 1, Math.floor(n)));
      state.session.range = { start: clamp(msg.start), end: clamp(msg.end) };
      provider.refresh(state.session.doc);
    }
  });
  // Seed with current data
  const entries = state.session ? (state.lastResults.get(state.session.doc.uri.toString()) ?? []) : [];
  updateTimelineWebview(entries);
}

function updateTimelineWebview(events: AnyEvent[]) {
  if (!state.timelinePanel) return;
  const total = Math.max(0, (state.lastResults.get(state.session?.doc.uri.toString() ?? '') ?? []).length);
  const range = state.session?.range ?? { start: 0, end: Math.max(0, total - 1) };
  const frames = buildFrames(events);
  state.timelinePanel.webview.postMessage({ type: 'frames', total, range, frames });
}

type Frame = { id: number; method: string; start: number; end: number; depth: number; line?: number };
function buildFrames(events: AnyEvent[]): Frame[] {
  const frames: Frame[] = [];
  const stack: { method: string; start: number; line?: number }[] = [];
  let nextId = 1;
  for (const ev of events) {
    const t = ev.type ?? 'line';
    if (t === 'callStart') {
      const m = String(ev.method ?? '');
      stack.push({ method: m, start: ev.__seq, line: ev.line });
    } else if (t === 'callEnd') {
      const m = String(ev.method ?? '');
      for (let i = stack.length - 1; i >= 0; i--) {
        if (stack[i].method === m) {
          const depth = i;
          const start = stack[i].start;
          const line = stack[i].line;
          frames.push({ id: nextId++, method: m, start, end: ev.__seq, depth, line });
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
    frames.push({ id: nextId++, method: f.method, start: f.start, end: lastSeq, depth: i, line: f.line });
  }
  return frames;
}

function getTimelineHtml(): string {
  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <style>
    body { font-family: var(--vscode-font-family); color: var(--vscode-foreground); }
    .wrap { padding: 12px; display: flex; flex-direction: column; gap: 8px; align-items: stretch; }
    .info { font-size: 12px; opacity: 0.8; }
    .timeline { position: relative; width: 100%; height: 320px; background: var(--vscode-editorWidget-background); border: 1px solid var(--vscode-panel-border); border-radius: 4px; overflow: auto; }
    .bar { position: absolute; border-radius: 2px; border: 1px solid var(--vscode-panel-border); cursor: pointer; display: block; opacity: 0.75; z-index: 1; }
    .bar:hover { outline: 1px solid var(--vscode-focusBorder); }
    .bar.active { opacity: 1; border-color: var(--vscode-focusBorder); }
    .label { display: none; }
    .tickH { position: absolute; left: 0; right: 0; height: 1px; background: var(--vscode-panel-border); opacity: 0.4; }
  </style>
  </head>
  <body>
    <div class="wrap">
      <div id="info" class="info"></div>
      <div id="timeline" class="timeline"></div>
    </div>
    <script>
      const vscode = acquireVsCodeApi();
      let total = 0; let start = 0; let end = 0; let frames = []; let activeId = null;
      const timeline = document.getElementById('timeline');
      const info = document.getElementById('info');

      window.addEventListener('message', (e) => {
        const msg = e.data;
        if (msg.type === 'frames') { total = msg.total || 0; start = clampIndex(msg.range?.start ?? 0); end = clampIndex(msg.range?.end ?? (total-1)); frames = Array.isArray(msg.frames) ? msg.frames : []; render(); }
      });

      function render() {
        timeline.innerHTML = '';
        const height = timeline.clientHeight || 320;
        const width = timeline.clientWidth || 600;
        const visible = frames;
        const padY = 8; const padX = 8; const thinW = 8; const gapX = 16; const columnWidth = thinW + gapX;
        const maxDepth = visible.reduce((m,f)=>Math.max(m,f.depth||0),0);
        // ticks (horizontal)
        const tickStep = Math.max(1, Math.floor(total / 20));
        for (let i = 0; i <= total; i += tickStep) {
          const t = document.createElement('div'); t.className = 'tickH'; t.style.top = (posY(i, height, padY)) + 'px'; timeline.appendChild(t);
        }
        for (const f of visible) {
          const colX = padX + f.depth * columnWidth;
          const bar = document.createElement('div'); bar.className = 'bar';
          const y1 = posY(f.start, height, padY); const y2 = posY(Math.max(f.end, f.start+1), height, padY);
          bar.style.left = colX + 'px'; bar.style.top = y1 + 'px'; bar.style.width = thinW + 'px'; bar.style.height = Math.max(2, y2 - y1) + 'px';
          bar.title = f.method + (f.line ? (' @ L' + f.line) : '');
          bar.addEventListener('click', (e)=>{ e.stopPropagation(); const s = clampIndex(f.start); const ee = clampIndex(f.end); start = s; end = ee; activeId = f.id; vscode.postMessage({ type: 'updateRange', start: s, end: ee }); render(); });
          // Color and active state
          const selected = frames.find(ff => ff.id === activeId);
          const isActive = selected ? (isAncestor(f, selected) || isDescendant(f, selected) || f.id === selected.id) : false;
          bar.className = 'bar' + (isActive ? ' active' : '');
          bar.style.backgroundColor = colorFor(f, isActive);
          timeline.appendChild(bar);
        }
        // spacer to extend scrollable width for deep stacks
        const spacer = document.createElement('div'); spacer.style.position='absolute'; spacer.style.left=(padX + (maxDepth+1)*columnWidth)+'px'; spacer.style.top='0'; spacer.style.width='1px'; spacer.style.height='1px'; spacer.style.pointerEvents='none'; timeline.appendChild(spacer);
        highlightSelection(height, padY);
        info.textContent = total>0 ? 'Events: '+total+'   Range: ['+start+'..'+end+'] ('+(end-start+1)+' entries)' : 'No event data yet';
      }
      function posY(index, height, padY) { if (total <= 1) return padY; const h = Math.max(1, height - padY*2); return Math.round(padY + (index/(total-1))*h); }
      function clampIndex(i) { return Math.max(0, Math.min((total>0?total-1:0), Math.floor(i))); }

      function isAncestor(a, b) { return a.start <= b.start && a.end >= b.end && a.depth <= b.depth; }
      function isDescendant(a, b) { return a.start >= b.start && a.end <= b.end && a.depth >= b.depth; }
      function colorFor(f, active) { const h = (hashCode(f.method||'') % 360 + 360) % 360; const s = active ? 70 : 55; const l = active ? 45 : 30; return 'hsl(' + h + ' ' + s + '% ' + l + '%)'; }
      function hashCode(str){ let h=0; for(let i=0;i<str.length;i++){ h=((h<<5)-h)+str.charCodeAt(i); h|=0; } return Math.abs(h); }

      function highlightSelection(height, padY) {
        let sel = document.getElementById('insightor-sel');
        if (!sel) { sel = document.createElement('div'); sel.id = 'insightor-sel'; sel.style.position = 'absolute'; sel.style.left = '0'; sel.style.right = '0'; sel.style.background='var(--vscode-editorHoverWidget-background)'; sel.style.opacity='0.2'; sel.style.pointerEvents='none'; timeline.appendChild(sel); }
        sel.style.zIndex = '0';
        sel.style.top = posY(start, height, padY)+'px'; sel.style.height = Math.max(2, posY(end, height, padY)-posY(start, height, padY))+'px';
      }
    </script>
  </body>
  </html>`;
}
