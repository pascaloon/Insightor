// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as vscode from 'vscode';
import { spawn } from 'child_process';
import * as path from 'path';

type ProbeEntry = { line: number; variables: Record<string, unknown> };

const state = {
  lastResults: new Map<string, ProbeEntry[]>(),
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
	const runCmd = vscode.commands.registerCommand('insightor.runProbes', async () => {
		const editor = vscode.window.activeTextEditor;
		if (!editor) {
			vscode.window.showWarningMessage('Open a C# file first.');
			return;
		}
		const doc = editor.document;
		if (doc.languageId !== 'csharp' && !doc.fileName.endsWith('.cs')) {
			vscode.window.showWarningMessage('Not a C# file.');
			return;
		}
		await doc.save();
		const outDir = context.globalStorageUri.fsPath;
		await vscode.workspace.fs.createDirectory(vscode.Uri.file(outDir));
		const outPath = path.join(outDir, 'insightor.out.jsonl');
		// Clear previous
		try { await vscode.workspace.fs.delete(vscode.Uri.file(outPath)); } catch {}

		const workspaceFolder = vscode.workspace.getWorkspaceFolder(doc.uri);
		if (!workspaceFolder) {
			vscode.window.showErrorMessage('Open a workspace to run Insightor.');
			return;
		}
		const cliProj = path.join(workspaceFolder.uri.fsPath, 'cli.insightor', 'Insightor', 'Insightor.csproj');
		const status = vscode.window.setStatusBarMessage('$(play) Insightor: running...');
		try {
			const proc = spawn('dotnet', ['run', '--project', cliProj, '--', doc.fileName, outPath], { cwd: workspaceFolder.uri.fsPath });
			let stderr = '';
			proc.stderr.on('data', d => { stderr += d.toString(); });
			await new Promise<void>((resolve, reject) => {
				proc.on('error', reject);
				proc.on('exit', code => code === 0 ? resolve() : reject(new Error('Exit ' + code + '\n' + stderr)));
			});
			const content = (await vscode.workspace.fs.readFile(vscode.Uri.file(outPath))).toString();
			const entries: ProbeEntry[] = content.split(/\r?\n/).filter(Boolean).map(l => JSON.parse(l));
			state.lastResults.set(doc.uri.toString(), entries);
			vscode.window.showInformationMessage(`Insightor: ${entries.length} probe lines`);
			inlayProvider.refresh(doc);
		} catch (err: any) {
			vscode.window.showErrorMessage('Insightor failed: ' + (err?.message ?? String(err)));
		} finally {
			status.dispose();
		}
	});

	const inlayProvider = new InsightorInlayProvider();
	context.subscriptions.push(
		vscode.languages.registerInlayHintsProvider({ language: 'csharp' }, inlayProvider),
		runCmd
	);
}

// This method is called when your extension is deactivated
export function deactivate() {}

class InsightorInlayProvider implements vscode.InlayHintsProvider {
  private _onDidChangeInlayHints = new vscode.EventEmitter<vscode.Uri | undefined>();
  onDidChangeInlayHints = this._onDidChangeInlayHints.event;

  refresh(doc?: vscode.TextDocument) {
    this._onDidChangeInlayHints.fire(doc?.uri);
  }

  provideInlayHints(document: vscode.TextDocument, range: vscode.Range, token: vscode.CancellationToken): vscode.ProviderResult<vscode.InlayHint[]> {
    const key = document.uri.toString();
    const entries = state.lastResults.get(key) ?? [];
    const hints: vscode.InlayHint[] = [];
    for (const entry of entries) {
      const lineIdx = entry.line - 1;
      if (lineIdx < 0 || lineIdx >= document.lineCount) continue;
      const line = document.lineAt(lineIdx);
      if (!range.contains(line.range)) continue;
      const text = Object.entries(entry.variables).map(([k, v]) => `${k}: ${formatValue(v)}`).join(', ');
      const position = new vscode.Position(lineIdx, line.range.end.character);
      const hint = new vscode.InlayHint(` // ${text}`, position, vscode.InlayHintKind.Type);
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
