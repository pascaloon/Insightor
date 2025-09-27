/**
 * Main entry point for the Insightor VS Code extension
 * Provides debugging and visualization tools for C# code execution
 */

import * as vscode from 'vscode';
import * as path from 'path';
import { state, stopCurrentSession, clearAnimation, clearAnimatorDecoration } from './core/state';
import { runInsightorNow, debouncedRun, ensureStorageDir, getFilteredLineEntries } from './utils/execution';
import { openOrRevealTimeline, openOrRevealVariablesTable, openOrRevealCallGraph, openOrRevealAnimator } from './ui/webviews';
import { updateTimelineWebview, pushVariablesData, pushCallGraphData } from './utils/dataProcessing';
import { pushAnimatorState, startAnimation, pauseAnimation, stopAnimation, setAnimationSpeed, stepAnimation, updateAnimatedView } from './utils/animation';

/**
 * VS Code extension activation entry point
 * Registers all commands and providers
 * @param context Extension context for managing subscriptions
 */
export function activate(context: vscode.ExtensionContext): void {
  console.log('Insightor extension is now active!');

  const inlayProvider = new InsightorInlayProvider();

  // Register command handlers
  const startSession = vscode.commands.registerCommand('insightor.startSession', async () => {
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

    await ensureStorageDir(context);
    stopCurrentSession();

    const tempPath = path.join(context.globalStorageUri.fsPath, 'insightor.session-buffer.cs');
    state.session = {
      doc,
      disposables: [],
      timer: null,
      builtDllPath: null,
      tempPath,
      range: { start: 0, end: Number.MAX_SAFE_INTEGER }
    };

    // Set up event listeners
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
    if (!editor) {
      vscode.window.showWarningMessage('Open a C# file first.');
      return;
    }

    const doc = editor.document;
    if (doc.languageId !== 'csharp' && !doc.fileName.endsWith('.cs')) {
      vscode.window.showWarningMessage('Not a C# file.');
      return;
    }

    const builtDllPath = state.session?.builtDllPath ?? null;
    const priorTemp = state.session?.tempPath;

    stopCurrentSession();

    const tempPath = priorTemp ?? path.join(context.globalStorageUri.fsPath, 'insightor.session-buffer.cs');
    state.session = {
      doc,
      disposables: [],
      timer: null,
      builtDllPath,
      tempPath,
      range: { start: 0, end: Number.MAX_SAFE_INTEGER }
    };

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

  const stopSession = vscode.commands.registerCommand('insightor.stopSession', () => {
    stopCurrentSession();
  });

  const showTimeline = vscode.commands.registerCommand('insightor.showTimeline', () => {
    openOrRevealTimeline(context, inlayProvider);
  });

  const showVariablesTable = vscode.commands.registerCommand('insightor.showVariablesTable', () => {
    openOrRevealVariablesTable(context, inlayProvider);
  });

  const showCallGraph = vscode.commands.registerCommand('insightor.showCallGraph', () => {
    openOrRevealCallGraph(context, inlayProvider);
  });

  const showAnimator = vscode.commands.registerCommand('insightor.showAnimator', () => {
    openOrRevealAnimator(context, inlayProvider);
  });

  // Register all subscriptions
  context.subscriptions.push(
    vscode.languages.registerInlayHintsProvider({ language: 'csharp' }, inlayProvider),
    startSession,
    restartSession,
    stopSession,
    showTimeline,
    showVariablesTable,
    showCallGraph,
    showAnimator
  );
}

/**
 * VS Code extension deactivation entry point
 */
export function deactivate(): void {
  // Clean up any running sessions
  stopCurrentSession();
  clearAnimation();
}

/**
 * Inlay hints provider for displaying variable values inline
 */
class InsightorInlayProvider implements vscode.InlayHintsProvider {
  private _onDidChangeInlayHints = new vscode.EventEmitter<void>();
  readonly onDidChangeInlayHints = this._onDidChangeInlayHints.event;

  /**
   * Refreshes the inlay hints for the given document
   * @param doc Optional document to refresh hints for
   */
  refresh(doc?: vscode.TextDocument): void {
    this._onDidChangeInlayHints.fire();
  }

  /**
   * Provides inlay hints for the given document and range
   * @param document Document to provide hints for
   * @param range Range to provide hints within
   * @param token Cancellation token
   * @returns Array of inlay hints
   */
  provideInlayHints(
    document: vscode.TextDocument,
    range: vscode.Range,
    token: vscode.CancellationToken
  ): vscode.ProviderResult<vscode.InlayHint[]> {
    const entries = getFilteredLineEntries(document);
    const hints: vscode.InlayHint[] = [];
    const byLine = new Map<number, typeof entries>();

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

/**
 * Formats a value for display in the UI
 * @param value Value to format
 * @returns Formatted string representation
 */
function formatValue(value: unknown): string {
  if (value === null || value === undefined) return 'null';
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}
