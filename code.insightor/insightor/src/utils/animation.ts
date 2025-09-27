import * as vscode from 'vscode';
import { state, clearAnimation } from '../core/state';
import type { AnyEvent } from '../core/types';

/**
 * Gets the entries within the current session range
 * @returns Array of events in the current range
 */
export function currentEntries(): AnyEvent[] {
  if (!state.session) return [];

  const all = state.lastResults.get(state.session.doc.uri.toString()) ?? [];
  const range = state.session.range;
  return all.filter(e => e.__seq >= range.start && e.__seq <= range.end);
}

/**
 * Starts the animation playback
 * @param provider Inlay hints provider for refreshing UI
 */
export function startAnimation(provider: any): void {
  if (!state.session) return;

  state.anim.playing = true;

  if (state.anim.timer) {
    clearInterval(state.anim.timer);
  }

  state.anim.timer = setInterval(() => stepAnimation(1, provider), state.anim.speedMs);
  pushAnimatorState();
  provider.refresh(state.session.doc);
}

/**
 * Pauses the animation playback
 */
export function pauseAnimation(): void {
  state.anim.playing = false;

  if (state.anim.timer) {
    clearInterval(state.anim.timer);
    state.anim.timer = null;
  }

  pushAnimatorState();
}

/**
 * Stops the animation and resets cursor position
 * @param provider Optional inlay hints provider for refreshing UI
 */
export function stopAnimation(provider?: any): void {
  pauseAnimation();

  // Reset cursor to disable animator filtering
  state.anim.cursorSeq = 0;
  updateAnimatedView();

  if (provider && state.session) {
    provider.refresh(state.session.doc);
  }
}

/**
 * Sets the animation playback speed
 * @param ms Milliseconds between steps
 * @param provider Inlay hints provider for refreshing UI
 */
export function setAnimationSpeed(ms: number, provider: any): void {
  state.anim.speedMs = Math.max(20, Math.min(5000, Math.floor(ms)));

  if (state.anim.playing) {
    startAnimation(provider);
  } else {
    pushAnimatorState();
  }
}

/**
 * Steps the animation forward or backward
 * @param delta Number of steps to move (positive for forward, negative for backward)
 * @param provider Inlay hints provider for refreshing UI
 */
export function stepAnimation(delta: number, provider: any): void {
  if (!state.session) return;

  const range = state.session.range;
  const entries = currentEntries();
  const lines = entries.filter(e => (e.type ?? 'line') === 'line' && typeof e.line === 'number');

  if (lines.length === 0) return;

  // Find block for current cursor
  let i = lines.findIndex(e => e.__seq >= state.anim.cursorSeq);
  if (i < 0) i = lines.length - 1;

  const currLine = lines[i].line as number;
  let start = i;
  while (start - 1 >= 0 && (lines[start - 1].line as number) === currLine) start--;

  let end = i;
  while (end + 1 < lines.length && (lines[end + 1].line as number) === currLine) end++;

  if (delta > 0) {
    if (state.anim.cursorSeq < lines[end].__seq) {
      state.anim.cursorSeq = lines[end].__seq;
    } else if (end + 1 < lines.length) {
      let j = end + 1;
      const nextLine = lines[j].line as number;
      while (j + 1 < lines.length && (lines[j + 1].line as number) === nextLine) j++;
      state.anim.cursorSeq = lines[j].__seq;
    } else {
      state.anim.cursorSeq = Math.min(range.end, lines[end].__seq);
    }
  } else if (delta < 0) {
    if (state.anim.cursorSeq > lines[start].__seq) {
      state.anim.cursorSeq = lines[start].__seq;
    } else if (start - 1 >= 0) {
      let j = start - 1;
      const prevLine = lines[j].line as number;
      while (j - 1 >= 0 && (lines[j - 1].line as number) === prevLine) j--;
      let k = j;
      while (k + 1 < lines.length && (lines[k + 1].line as number) === prevLine) k++;
      state.anim.cursorSeq = lines[k].__seq;
    } else {
      state.anim.cursorSeq = Math.max(range.start, lines[start].__seq);
    }
  }

  updateAnimatedView();
  if (state.session) {
    provider.refresh(state.session.doc);
  }
  pushAnimatorState();
}

/**
 * Updates the visual representation of the animated view
 */
export function updateAnimatedView(): void {
  if (!state.session) return;

  // Update decoration
  ensureAnimatorDecoration();

  const editor = vscode.window.visibleTextEditors.find(
    ed => state.session && ed.document.uri.toString() === state.session.doc.uri.toString()
  ) || vscode.window.activeTextEditor;

  const entries = currentEntries().filter(e => (e.type ?? 'line') === 'line');
  const curr = entries.find(e => e.__seq >= state.anim.cursorSeq) ?? entries[entries.length - 1];

  if (editor && curr && typeof curr.line === 'number') {
    const idx = Math.max(0, (curr.line as number) - 1);
    const line = editor.document.lineAt(Math.min(idx, editor.document.lineCount - 1));
    editor.setDecorations(state.anim.deco!, [new vscode.Range(line.range.start, line.range.start)]);

    // Select the line and scroll into view
    editor.selections = [new vscode.Selection(line.range.start, line.range.end)];
    editor.revealRange(line.range, vscode.TextEditorRevealType.InCenterIfOutsideViewport);
  }
}

/**
 * Creates the animator decoration type if it doesn't exist
 */
export function ensureAnimatorDecoration(): void {
  if (state.anim.deco) return;

  state.anim.deco = vscode.window.createTextEditorDecorationType({
    gutterIconPath: vscode.Uri.parse(
      'data:image/svg+xml;utf8,' +
      encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 16 16">' +
        '<polygon points="3,2 13,8 3,14" fill="#FFD700"/>' +
        '</svg>')
    ),
    gutterIconSize: 'contain',
  });
}

/**
 * Sends current animation state to the animator webview
 */
export function pushAnimatorState(): void {
  if (!state.animatorPanel) return;

  state.animatorPanel.webview.postMessage({
    type: 'state',
    speedMs: state.anim.speedMs,
    cursorSeq: state.anim.cursorSeq,
    playing: state.anim.playing
  });
}
