import * as vscode from 'vscode';
import { state, clearAnimatorDecoration } from '../core/state';
import { createTimelineHtml, createVariablesHtml, createCallGraphHtml, createAnimatorHtml } from './templates';
import { updateTimelineWebview, pushVariablesData, pushCallGraphData } from '../utils/dataProcessing';
import { pushAnimatorState, startAnimation, pauseAnimation, stopAnimation, setAnimationSpeed, stepAnimation } from '../utils/animation';

/**
 * Opens or reveals the timeline webview
 * @param context VS Code extension context
 * @param provider Inlay hints provider for refreshing UI
 */
export function openOrRevealTimeline(context: vscode.ExtensionContext, provider: any): void {
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
  panel.webview.html = createTimelineHtml();

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

/**
 * Opens or reveals the variables table webview
 * @param context VS Code extension context
 * @param provider Inlay hints provider for refreshing UI
 */
export function openOrRevealVariablesTable(context: vscode.ExtensionContext, provider: any): void {
  if (state.variablesPanel) {
    state.variablesPanel.reveal();
    pushVariablesData();
    return;
  }

  const panel = vscode.window.createWebviewPanel(
    'insightorVariables',
    'Insightor Variables Table',
    vscode.ViewColumn.Beside,
    { enableScripts: true, retainContextWhenHidden: true }
  );

  state.variablesPanel = panel;
  panel.onDidDispose(() => { state.variablesPanel = null; });
  panel.webview.html = createVariablesHtml();

  panel.webview.onDidReceiveMessage(msg => {
    // currently no inbound messages
  });

  // React to selection changes to refresh table
  const selSub = vscode.window.onDidChangeTextEditorSelection(e => {
    if (!state.variablesPanel) return;
    if (!state.session || e.textEditor.document.uri.toString() !== state.session.doc.uri.toString()) return;
    pushVariablesData();
  });

  const closeSub = vscode.workspace.onDidCloseTextDocument(d => {
    if (state.session?.doc.uri.toString() === d.uri.toString()) {
      pushVariablesData();
    }
  });

  state.session?.disposables.push(selSub, closeSub);
  // Seed
  pushVariablesData();
}

/**
 * Opens or reveals the call graph webview
 * @param context VS Code extension context
 * @param provider Inlay hints provider for refreshing UI
 */
export function openOrRevealCallGraph(context: vscode.ExtensionContext, provider: any): void {
  if (state.callGraphPanel) {
    state.callGraphPanel.reveal();
    pushCallGraphData();
    return;
  }

  const panel = vscode.window.createWebviewPanel(
    'insightorCallGraph',
    'Insightor Call Graph',
    vscode.ViewColumn.Beside,
    { enableScripts: true, retainContextWhenHidden: true }
  );

  state.callGraphPanel = panel;
  panel.onDidDispose(() => { state.callGraphPanel = null; });
  panel.webview.html = createCallGraphHtml();

  const selSub = vscode.window.onDidChangeTextEditorSelection(e => {
    if (!state.session) return;
    if (e.textEditor.document.uri.toString() !== state.session.doc.uri.toString()) return;
    pushCallGraphData();
  });

  state.session?.disposables.push(selSub);
  pushCallGraphData();
}

/**
 * Opens or reveals the animator webview
 * @param context VS Code extension context
 * @param provider Inlay hints provider for refreshing UI
 */
export function openOrRevealAnimator(context: vscode.ExtensionContext, provider: any): void {
  if (state.animatorPanel) {
    state.animatorPanel.reveal();
    pushAnimatorState();
    return;
  }

  const panel = vscode.window.createWebviewPanel(
    'insightorAnimator',
    'Insightor Animator',
    vscode.ViewColumn.Beside,
    { enableScripts: true, retainContextWhenHidden: true }
  );

  state.animatorPanel = panel;
  panel.onDidDispose(() => {
    state.animatorPanel = null;
    stopAnimation();
    clearAnimatorDecoration();
  });

  panel.webview.html = createAnimatorHtml();

  panel.webview.onDidReceiveMessage(msg => {
    if (!state.session) return;
    if (msg?.type === 'play') { startAnimation(provider); }
    if (msg?.type === 'pause') { pauseAnimation(); }
    if (msg?.type === 'stop') { stopAnimation(provider); }
    if (msg?.type === 'speed' && typeof msg.ms === 'number') { setAnimationSpeed(msg.ms, provider); }
    if (msg?.type === 'stepNext') { stepAnimation(+1, provider); }
    if (msg?.type === 'stepPrev') { stepAnimation(-1, provider); }
  });

  pushAnimatorState();
}
