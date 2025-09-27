import * as vscode from 'vscode';
import { spawn } from 'child_process';
import * as path from 'path';
import { state } from '../core/state';
import type { AnyEvent, ExtendedProbeEntry } from '../core/types';
import { updateTimelineWebview, pushVariablesData, pushCallGraphData } from './dataProcessing';

/**
 * Executes the Insightor CLI with the given arguments
 * @param args Command line arguments for the CLI
 * @param cwd Working directory for execution
 * @returns Promise that resolves when execution completes
 */
export function execDotnet(args: string[], cwd: string): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    const proc = spawn('dotnet', args, { cwd });
    let stderr = '';

    proc.stderr.on('data', (data: Buffer) => {
      stderr += data.toString();
    });

    proc.on('error', reject);
    proc.on('exit', (code: number | null) => {
      if (code === 0) {
        resolve();
      } else {
        reject(new Error(`Exit ${code}\n${stderr}`));
      }
    });
  });
}

/**
 * Runs Insightor analysis on the current document
 * @param context VS Code extension context
 * @param provider Inlay hints provider for refreshing UI
 */
export async function runInsightorNow(
  context: vscode.ExtensionContext,
  provider: any
): Promise<void> {
  if (!state.session) return;

  const doc = state.session.doc;
  const outPath = path.join(context.globalStorageUri.fsPath, 'insightor.out.jsonl');

  try {
    // Clear previous output
    await vscode.workspace.fs.delete(vscode.Uri.file(outPath));
  } catch {
    // File might not exist, ignore error
  }

  // Write current buffer to temp file to avoid forcing a save
  const tempPath = state.session.tempPath;
  const buffer = Buffer.from(doc.getText(), 'utf8');
  await vscode.workspace.fs.writeFile(vscode.Uri.file(tempPath), buffer);

  const workspaceFolder = vscode.workspace.getWorkspaceFolder(doc.uri);
  if (!workspaceFolder) {
    vscode.window.showErrorMessage('Open a workspace to run Insightor.');
    return;
  }

  const extDir = vscode.extensions.getExtension('insightor')?.extensionUri.fsPath ??
                 context.extensionUri.fsPath;
  const cliProj = path.resolve(extDir, '..', '..', 'cli.insightor', 'Insightor', 'Insightor.csproj');
  const dllPath = path.resolve(extDir, '..', '..', 'cli.insightor', 'Insightor', 'bin', 'Debug', 'net9.0', 'Insightor.dll');

  const status = vscode.window.setStatusBarMessage('$(play) Insightor: running...');

  try {
    if (!state.session.builtDllPath) {
      // First run - build and run the project
      await execDotnet(['run', '--project', cliProj, '--', tempPath, outPath], path.dirname(cliProj));
      // After first successful run, reuse built dll
      state.session.builtDllPath = dllPath;
    } else {
      // Subsequent runs - use pre-built dll
      await execDotnet([state.session.builtDllPath, tempPath, outPath], path.dirname(cliProj));
    }

    // Parse and store results
    const content = (await vscode.workspace.fs.readFile(vscode.Uri.file(outPath))).toString();
    const events: AnyEvent[] = content
      .split(/\r?\n/)
      .filter(Boolean)
      .map((line, index) => {
        const raw: any = JSON.parse(line);
        const type = (raw?.type as AnyEvent['type']) ?? 'line';

        if (type === 'line') {
          return {
            type,
            line: raw.line as number,
            variables: (raw.variables ?? {}) as Record<string, unknown>,
            __seq: index
          } as AnyEvent;
        }

        if (type === 'callStart' || type === 'callEnd') {
          return {
            type,
            method: String(raw.method ?? ''),
            line: raw.line as number | undefined,
            variables: (raw.variables ?? {}) as Record<string, unknown>,
            __seq: index
          } as AnyEvent;
        }

        return {
          type: 'line',
          line: raw.line as number,
          variables: (raw.variables ?? {}) as Record<string, unknown>,
          __seq: index
        } as AnyEvent;
      });

    state.lastResults.set(doc.uri.toString(), events);
    provider.refresh(doc);

    // Update all webviews
    updateTimelineWebview(events);
    pushVariablesData();
    pushCallGraphData();

  } catch (err: any) {
    vscode.window.showErrorMessage('Insightor failed: ' + (err?.message ?? String(err)));
  } finally {
    status.dispose();
  }
}

/**
 * Filters line entries based on current session range and animation state
 * @param document VS Code document
 * @returns Array of filtered probe entries
 */
export function getFilteredLineEntries(document: vscode.TextDocument): ExtendedProbeEntry[] {
  const key = document.uri.toString();
  const entries = state.lastResults.get(key) ?? [];
  const range = state.session?.range ?? { start: 0, end: Number.MAX_SAFE_INTEGER };

  let filtered = entries.filter(e =>
    e.__seq >= range.start &&
    e.__seq <= range.end &&
    (e.type ?? 'line') === 'line' &&
    e.line != null &&
    e.variables != null
  );

  // If animator playing/stepping, show only the current step's probe
  if (state.anim.playing || state.anim.cursorSeq > 0) {
    const curr = filtered.find(e => e.__seq >= state.anim.cursorSeq) ?? filtered[filtered.length - 1];
    filtered = curr ? [curr] : [];
  }

  return filtered.map(e => ({
    line: e.line as number,
    variables: e.variables as Record<string, unknown>,
    __seq: e.__seq
  }));
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

/**
 * Debounces function calls to avoid excessive execution
 * @param context VS Code extension context
 * @param provider Inlay hints provider for refreshing UI
 * @param delay Delay in milliseconds
 */
export function debouncedRun(context: vscode.ExtensionContext, provider: any, delay = 350): void {
  if (!state.session) return;
  if (state.session.timer) clearTimeout(state.session.timer);
  state.session.timer = setTimeout(() => {
    runInsightorNow(context, provider).catch(() => {});
  }, delay);
}

/**
 * Ensures the storage directory exists
 * @param context VS Code extension context
 */
export async function ensureStorageDir(context: vscode.ExtensionContext): Promise<void> {
  const outDir = context.globalStorageUri.fsPath;
  await vscode.workspace.fs.createDirectory(vscode.Uri.file(outDir));
}
