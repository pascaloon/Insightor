/**
 * Core type definitions for the Insightor VS Code extension
 */

export type AnyEvent = {
  type?: 'line' | 'callStart' | 'callEnd';
  line?: number;
  method?: string;
  variables?: Record<string, unknown>;
  __seq: number;
};

export type ProbeEntry = {
  line: number;
  variables: Record<string, unknown>;
};

export type ExtendedProbeEntry = ProbeEntry & {
  __seq: number;
};

export type Frame = {
  id: number;
  method: string;
  start: number;
  end: number;
  depth: number;
  line?: number;
  args?: Record<string, unknown>;
};

export type SessionState = {
  doc: any; // vscode.TextDocument
  disposables: any[]; // vscode.Disposable[]
  timer: NodeJS.Timeout | null;
  builtDllPath: string | null;
  tempPath: string;
  range: { start: number; end: number };
};

export type AnimationState = {
  playing: boolean;
  speedMs: number;
  timer: NodeJS.Timeout | null;
  cursorSeq: number;
  deco: any | null; // vscode.TextEditorDecorationType
};

export type AppState = {
  lastResults: Map<string, AnyEvent[]>;
  session: SessionState | null;
  timelinePanel: any | null; // vscode.WebviewPanel
  variablesPanel: any | null; // vscode.WebviewPanel
  callGraphPanel: any | null; // vscode.WebviewPanel
  animatorPanel: any | null; // vscode.WebviewPanel
  anim: AnimationState;
};
