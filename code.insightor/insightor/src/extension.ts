// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as vscode from 'vscode';
import { spawn } from 'child_process';
import * as path from 'path';

type ProbeEntry = { line: number; variables: Record<string, unknown> };
type ExtendedProbeEntry = ProbeEntry & { __seq: number };

const state = {
  lastResults: new Map<string, ExtendedProbeEntry[]>(),
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
    const entries = getFilteredEntries(document);
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
    const entries: ExtendedProbeEntry[] = content.split(/\r?\n/)
      .filter(Boolean)
      .map((l, i) => ({ ...(JSON.parse(l) as ProbeEntry), __seq: i }));
    state.lastResults.set(doc.uri.toString(), entries);
    provider.refresh(doc);
    updateTimelineWebview(entries);
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

function getFilteredEntries(document: vscode.TextDocument): ExtendedProbeEntry[] {
  const key = document.uri.toString();
  const entries = state.lastResults.get(key) ?? [];
  const range = state.session?.range ?? { start: 0, end: Number.MAX_SAFE_INTEGER };
  return entries.filter(e => e.__seq >= range.start && e.__seq <= range.end);
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

function updateTimelineWebview(entries: ExtendedProbeEntry[]) {
  if (!state.timelinePanel) return;
  const total = entries.length;
  const range = state.session?.range ?? { start: 0, end: Math.max(0, total - 1) };
  state.timelinePanel.webview.postMessage({ type: 'data', total, range });
}

function getTimelineHtml(): string {
  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <style>
    body { font-family: var(--vscode-font-family); color: var(--vscode-foreground); }
    .wrap { padding: 12px; display: flex; flex-direction: column; gap: 8px; align-items: flex-start; }
    .bar { position: relative; width: 32px; height: 320px; background: var(--vscode-editorWidget-background); border: 1px solid var(--vscode-panel-border); border-radius: 4px; }
    .notches { position: absolute; left: 14px; top: 4px; bottom: 4px; width: 4px; }
    .notches div { position: absolute; left: 0; width: 4px; height: 2px; background: var(--vscode-focusBorder); opacity: 0.6; }
    .range { position: absolute; left: 0; right: 0; background: var(--vscode-editorHoverWidget-background); opacity: 0.35; }
    .handle { position: absolute; left: 0; right: 0; height: 8px; background: var(--vscode-focusBorder); cursor: ns-resize; }
    .info { font-size: 12px; opacity: 0.8; }
  </style>
  </head>
  <body>
    <div class="wrap">
      <div id="info" class="info"></div>
      <div id="bar" class="bar">
        <div id="notches" class="notches"></div>
        <div id="range" class="range"></div>
        <div id="start" class="handle"></div>
        <div id="end" class="handle"></div>
      </div>
    </div>
    <script>
      const vscode = acquireVsCodeApi();
      let total = 0; let start = 0; let end = 0;
      const bar = document.getElementById('bar');
      const notches = document.getElementById('notches');
      const range = document.getElementById('range');
      const hStart = document.getElementById('start');
      const hEnd = document.getElementById('end');
      const info = document.getElementById('info');

      window.addEventListener('message', (e) => {
        const msg = e.data;
        if (msg.type === 'data') {
          total = msg.total || 0;
          start = Math.max(0, Math.min(total-1, (msg.range?.start ?? 0)));
          end = Math.max(start, Math.min(total-1, (msg.range?.end ?? (total-1))));
          render();
        }
      });

      function render() {
        const h = bar.clientHeight || 320;
        notches.innerHTML = '';
        if (total > 0) {
          const step = Math.max(1, Math.floor(total / Math.max(40, Math.min(160, h/6))));
          for (let i=0;i<total;i+=step){ const d=document.createElement('div'); d.style.top = ((i)/(total-1))*100+'%'; notches.appendChild(d);}  
        }
        const ys = posFor(start), ye = posFor(end);
        range.style.top = ys+'px';
        range.style.height = Math.max(2, ye-ys)+'px';
        hStart.style.top = (ys-4)+'px';
        hEnd.style.top = (ye-4)+'px';
        info.textContent = total>0 ? 
          'Probes: '+total+'   Range: ['+start+'..'+end+'] ('+(end-start+1)+' entries)' : 'No probe data yet';
      }

      function posFor(index){ const h=bar.clientHeight||320; if(total<=1) return 0; return Math.round((index/(total-1))*h); }
      function indexFor(py){ const h=bar.clientHeight||320; if(h<=0||total<=1) return 0; return Math.round((py/h)*(total-1)); }

      function bindDrag(el, onMove){ let dragging=false; el.addEventListener('mousedown',e=>{dragging=true; e.preventDefault();}); window.addEventListener('mousemove',e=>{ if(!dragging) return; onMove(e.clientY); vscode.postMessage({type:'updateRange', start, end}); }); window.addEventListener('mouseup',()=>{ dragging=false; }); }
      bindDrag(hStart, (cy)=>{ const rect=bar.getBoundingClientRect(); const idx=indexFor(cy-rect.top); start = Math.max(0, Math.min(end, idx)); render(); });
      bindDrag(hEnd, (cy)=>{ const rect=bar.getBoundingClientRect(); const idx=indexFor(cy-rect.top); end = Math.max(start, Math.min(total-1, idx)); render(); });

      // Click to jump
      bar.addEventListener('click', (e)=>{ const rect=bar.getBoundingClientRect(); const idx=indexFor(e.clientY-rect.top); const ds=Math.abs(idx-start), de=Math.abs(idx-end); if(ds<de){ start = Math.min(idx,end); } else { end = Math.max(idx,start); } render(); vscode.postMessage({type:'updateRange', start, end}); });
    </script>
  </body>
  </html>`;
}
