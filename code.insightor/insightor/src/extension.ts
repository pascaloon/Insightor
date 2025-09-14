// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as vscode from 'vscode';
import { spawn } from 'child_process';
import * as path from 'path';

type ProbeEntry = { line: number; variables: Record<string, unknown> };

const state = {
  lastResults: new Map<string, ProbeEntry[]>(),
  session: null as null | {
    doc: vscode.TextDocument,
    disposables: vscode.Disposable[],
    timer: NodeJS.Timeout | null,
    builtDllPath: string | null,
    tempPath: string,
  },
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
		state.session = { doc, disposables: [], timer: null, builtDllPath: null, tempPath };
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
		state.session = { doc, disposables: [], timer: null, builtDllPath, tempPath };
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

	context.subscriptions.push(
		vscode.languages.registerInlayHintsProvider({ language: 'csharp' }, inlayProvider),
		startSession,
		restartSession,
		stopSession
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
    const entries = state.lastResults.get(key) ?? [];
    const hints: vscode.InlayHint[] = [];
    const byLine = new Map<number, ProbeEntry[]>();
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
    const entries: ProbeEntry[] = content.split(/\r?\n/).filter(Boolean).map(l => JSON.parse(l));
    state.lastResults.set(doc.uri.toString(), entries);
    provider.refresh(doc);
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
