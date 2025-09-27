import * as vscode from 'vscode';
import type { AnyEvent, AppState } from './types';

/**
 * Global application state management
 */
export const state: AppState = {
  lastResults: new Map<string, AnyEvent[]>(),
  session: null,
  timelinePanel: null,
  variablesPanel: null,
  callGraphPanel: null,
  animatorPanel: null,
  anim: {
    playing: false,
    speedMs: 600,
    timer: null,
    cursorSeq: 0,
    deco: null,
  }
};

/**
 * Stops the current debugging session and cleans up resources
 */
export function stopCurrentSession(): void {
  if (!state.session) return;

  if (state.session.timer) {
    clearTimeout(state.session.timer);
  }

  for (const disposable of state.session.disposables) {
    try {
      disposable.dispose();
    } catch {
      // Ignore disposal errors
    }
  }

  state.session = null;
}

/**
 * Clears the current animation state
 */
export function clearAnimation(): void {
  state.anim.playing = false;
  state.anim.cursorSeq = 0;

  if (state.anim.timer) {
    clearInterval(state.anim.timer);
    state.anim.timer = null;
  }

  clearAnimatorDecoration();
}

/**
 * Clears the animator decoration from the active editor
 */
export function clearAnimatorDecoration(): void {
  const editor = vscode.window.activeTextEditor;
  if (state.anim.deco && editor) {
    editor.setDecorations(state.anim.deco, []);
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
