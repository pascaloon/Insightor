/**
 * HTML templates for Insightor webviews
 * Replaces hardcoded HTML strings with structured, readable templates
 */

export interface TimelineTemplateData {
  // Template data will be added as needed
}

export interface VariablesTemplateData {
  // Template data will be added as needed
}

export interface CallGraphTemplateData {
  // Template data will be added as needed
}

export interface AnimatorTemplateData {
  // Template data will be added as needed
}

/**
 * Creates the timeline webview HTML
 * @returns HTML string for the timeline view
 */
export function createTimelineHtml(): string {
  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <style>
    ${getTimelineStyles()}
  </style>
</head>
<body>
  <div class="wrap">
    <div id="info" class="info"></div>
    <div id="timeline" class="timeline"></div>
  </div>
  <script>
    ${getTimelineScript()}
  </script>
</body>
</html>`;
}

/**
 * Creates the variables table webview HTML
 * @returns HTML string for the variables table view
 */
export function createVariablesHtml(): string {
  return `<!DOCTYPE html>
<html>
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <style>
    ${getVariablesStyles()}
  </style>
</head>
<body>
  <div class="wrap">
    <div id="host"></div>
  </div>
  <script>
    ${getVariablesScript()}
  </script>
</body>
</html>`;
}

/**
 * Creates the call graph webview HTML
 * @returns HTML string for the call graph view
 */
export function createCallGraphHtml(): string {
  return `<!DOCTYPE html>
<html>
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <style>
    ${getCallGraphStyles()}
  </style>
</head>
<body>
  <div class="wrap">
    <div id="graph" class="graph"></div>
  </div>
  <script>
    ${getCallGraphScript()}
  </script>
</body>
</html>`;
}

/**
 * Creates the animator webview HTML
 * @returns HTML string for the animator view
 */
export function createAnimatorHtml(): string {
  return `<!DOCTYPE html>
<html>
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <style>
    ${getAnimatorStyles()}
  </style>
</head>
<body>
  <div class="wrap">
    <button id="play">Play</button>
    <button id="pause">Pause</button>
    <button id="stop">Stop</button>
    <button id="prev">◀</button>
    <button id="next">▶</button>
    <span class="label">Speed (ms/step)</span>
    <input id="speed" type="range" min="60" max="2000" step="20" />
    <span id="info" class="label"></span>
  </div>
  <script>
    ${getAnimatorScript()}
  </script>
</body>
</html>`;
}

/**
 * Timeline view styles
 */
function getTimelineStyles(): string {
  return `
body { font-family: var(--vscode-font-family); color: var(--vscode-foreground); }
.wrap { padding: 12px; display: flex; flex-direction: column; gap: 8px; align-items: stretch; }
.info { font-size: 12px; opacity: 0.8; }
.timeline { position: relative; width: 100%; height: 320px; background: var(--vscode-editorWidget-background); border: 1px solid var(--vscode-panel-border); border-radius: 4px; overflow: auto; }
.bar { position: absolute; border-radius: 2px; border: 1px solid var(--vscode-panel-border); cursor: pointer; display: block; opacity: 0.75; z-index: 1; }
.bar:hover { outline: 1px solid var(--vscode-focusBorder); }
.bar.active { opacity: 1; border-color: var(--vscode-focusBorder); }
.label { display: none; }
.tickH { position: absolute; left: 0; right: 0; height: 1px; background: var(--vscode-panel-border); opacity: 0.4; }`;
}

/**
 * Variables table styles
 */
function getVariablesStyles(): string {
  return `
body { font-family: var(--vscode-font-family); color: var(--vscode-foreground); }
.wrap { padding: 12px; display: flex; flex-direction: column; gap: 12px; }
.table { border: 1px solid var(--vscode-panel-border); border-radius: 4px; overflow: auto; }
table { border-collapse: collapse; width: 100%; }
th, td { border: 1px solid var(--vscode-panel-border); padding: 4px 6px; font-size: 12px; }
th { background: var(--vscode-editorWidget-background); position: sticky; top: 0; z-index: 1; }
.title { font-weight: bold; font-size: 12px; opacity: 0.9; }
.empty { opacity: 0.7; font-size: 12px; }`;
}

/**
 * Call graph styles
 */
function getCallGraphStyles(): string {
  return `
body { font-family: var(--vscode-font-family); color: var(--vscode-foreground); }
.wrap { padding: 12px; }
.graph { position: relative; width: 100%; height: 360px; border: 1px solid var(--vscode-panel-border); border-radius: 4px; overflow: auto; background: var(--vscode-editorWidget-background); }
.node { position: absolute; width: 220px; padding: 6px 8px; border: 1px solid var(--vscode-panel-border); border-radius: 4px; background: var(--vscode-editorHoverWidget-background); font-size: 12px; }
.edge { position: absolute; height: 2px; background: var(--vscode-foreground); opacity: 0.5; transform-origin: 0 0; z-index: 0; }
.title { font-weight: bold; margin-bottom: 4px; }
.kv { font-family: var(--vscode-editor-font-family, monospace); font-size: 11px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }`;
}

/**
 * Animator styles
 */
function getAnimatorStyles(): string {
  return `
body { font-family: var(--vscode-font-family); color: var(--vscode-foreground); }
.wrap { padding: 12px; display: flex; gap: 8px; align-items: center; }
button { padding: 4px 10px; background: var(--vscode-button-background); color: var(--vscode-button-foreground); border: 1px solid var(--vscode-button-border, transparent); border-radius: 4px; cursor: pointer; }
button:hover { background: var(--vscode-button-hoverBackground); }
input[type=range] { width: 160px; }
.label { font-size: 12px; opacity: 0.8; }`;
}

/**
 * Timeline view script
 */
function getTimelineScript(): string {
  return `
const vscode = acquireVsCodeApi();
let total = 0; let start = 0; let end = 0; let frames = []; let activeId = null;
const timeline = document.getElementById('timeline');
const info = document.getElementById('info');

window.addEventListener('message', (e) => {
  const msg = e.data;
  if (msg.type === 'frames') {
    total = msg.total || 0;
    start = clampIndex(msg.range?.start ?? 0);
    end = clampIndex(msg.range?.end ?? (total-1));
    frames = Array.isArray(msg.frames) ? msg.frames : [];
    render();
  }
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
    const t = document.createElement('div');
    t.className = 'tickH';
    t.style.top = (posY(i, height, padY)) + 'px';
    timeline.appendChild(t);
  }

  for (const f of visible) {
    const colX = padX + f.depth * columnWidth;
    const bar = document.createElement('div');
    bar.className = 'bar';
    const y1 = posY(f.start, height, padY);
    const y2 = posY(Math.max(f.end, f.start+1), height, padY);
    bar.style.left = colX + 'px';
    bar.style.top = y1 + 'px';
    bar.style.width = thinW + 'px';
    bar.style.height = Math.max(2, y2 - y1) + 'px';

    // Build tooltip with arguments if available
    const argsTip = f.args ? Object.entries(f.args).map(([k,v])=>k+': '+stringify(v)).join(', ') : '';
    const pidx = f.method.indexOf('(');
    const base = pidx >= 0 ? f.method.slice(0, pidx) : f.method;
    bar.title = base + '(' + argsTip + ')' + (f.line ? (' @ L' + f.line) : '');

    bar.addEventListener('click', (e)=>{
      e.stopPropagation();
      const s = clampIndex(f.start);
      const ee = clampIndex(f.end);
      start = s;
      end = ee;
      activeId = f.id;
      vscode.postMessage({ type: 'updateRange', start: s, end: ee });
      render();
    });

    // Color and active state
    const selected = frames.find(ff => ff.id === activeId);
    const isActive = selected ? (isAncestor(f, selected) || isDescendant(f, selected) || f.id === selected.id) : false;
    bar.className = 'bar' + (isActive ? ' active' : '');
    bar.style.backgroundColor = colorFor(f, isActive);
    timeline.appendChild(bar);
  }

  // spacer to extend scrollable width for deep stacks
  const spacer = document.createElement('div');
  spacer.style.position='absolute';
  spacer.style.left=(padX + (maxDepth+1)*columnWidth)+'px';
  spacer.style.top='0';
  spacer.style.width='1px';
  spacer.style.height='1px';
  spacer.style.pointerEvents='none';
  timeline.appendChild(spacer);

  highlightSelection(height, padY);
  info.textContent = total>0 ? 'Events: '+total+'   Range: ['+start+'..'+end+'] ('+(end-start+1)+' entries)' : 'No event data yet';
}

function posY(index, height, padY) {
  if (total <= 1) return padY;
  const h = Math.max(1, height - padY*2);
  return Math.round(padY + (index/(total-1))*h);
}

function clampIndex(i) {
  return Math.max(0, Math.min((total>0?total-1:0), Math.floor(i)));
}

function isAncestor(a, b) {
  return a.start <= b.start && a.end >= b.end && a.depth <= b.depth;
}

function isDescendant(a, b) {
  return a.start >= b.start && a.end <= b.end && a.depth >= b.depth;
}

function colorFor(f, active) {
  const h = (hashCode(f.method||'') % 360 + 360) % 360;
  const s = active ? 70 : 55;
  const l = active ? 45 : 30;
  return 'hsl(' + h + ' ' + s + '% ' + l + '%)';
}

function hashCode(str){
  let h=0;
  for(let i=0;i<str.length;i++){
    h=((h<<5)-h)+str.charCodeAt(i);
    h|=0;
  }
  return Math.abs(h);
}

function highlightSelection(height, padY) {
  let sel = document.getElementById('insightor-sel');
  if (!sel) {
    sel = document.createElement('div');
    sel.id = 'insightor-sel';
    sel.style.position = 'absolute';
    sel.style.left = '0';
    sel.style.right = '0';
    sel.style.background='var(--vscode-editorHoverWidget-background)';
    sel.style.opacity='0.2';
    sel.style.pointerEvents='none';
    timeline.appendChild(sel);
  }
  sel.style.zIndex = '0';
  sel.style.top = posY(start, height, padY)+'px';
  sel.style.height = Math.max(2, posY(end, height, padY)-posY(start, height, padY))+'px';
}

function stringify(v){
  try {
    if (v===null||v===undefined) return 'null';
    if (typeof v==='object') return JSON.stringify(v);
    return String(v);
  } catch { return String(v); }
}`;
}

/**
 * Variables table script
 */
function getVariablesScript(): string {
  return `
const vscode = acquireVsCodeApi();
const host = document.getElementById('host');

window.addEventListener('message', e => {
  const msg = e.data;
  if (msg.type === 'tables') render(msg.tables||[]);
});

function render(tables){
  host.innerHTML = '';
  if (!tables.length){
    const d=document.createElement('div');
    d.className='empty';
    d.textContent='No selected active lines.';
    host.appendChild(d);
    return;
  }

  for (const t of tables) {
    const block = document.createElement('div');
    const title = document.createElement('div');
    title.className='title';
    title.textContent = 'Line ' + t.line;

    const wrap = document.createElement('div');
    wrap.className = 'table';
    const tbl = document.createElement('table');

    const thead = document.createElement('thead');
    const thr = document.createElement('tr');
    const th0 = document.createElement('th');
    th0.textContent = 'Variable';
    thr.appendChild(th0);

    for (const c of t.cols){
      const th=document.createElement('th');
      th.textContent = String(c);
      thr.appendChild(th);
    }

    thead.appendChild(thr);
    tbl.appendChild(thead);

    const tbody = document.createElement('tbody');
    for (const row of t.rows){
      const tr = document.createElement('tr');
      const td0=document.createElement('td');
      td0.textContent=row.name;
      tr.appendChild(td0);

      for (const v of row.values){
        const td=document.createElement('td');
        td.textContent = v==null?'':String(v);
        tr.appendChild(td);
      }

      tbody.appendChild(tr);
    }

    tbl.appendChild(tbody);
    wrap.appendChild(tbl);
    block.appendChild(title);
    block.appendChild(wrap);
    host.appendChild(block);
  }
}`;
}

/**
 * Call graph script
 */
function getCallGraphScript(): string {
  return `
const graph = document.getElementById('graph');

window.addEventListener('message', e => {
  const msg = e.data;
  if (msg.type === 'graph') render(msg.nodes||[], msg.edges||[], msg.annotations||{});
});

function hashCode(str){
  let h=0;
  for(let i=0;i<str.length;i++){
    h=((h<<5)-h)+str.charCodeAt(i);
    h|=0;
  }
  return Math.abs(h);
}

function colorFor(method){
  const h=(hashCode(method)%360+360)%360;
  const s=70;
  const l=45;
  return 'hsl(' + h + ' ' + s + '% ' + l + '%)';
}

function render(nodes, edges, annotations){
  graph.innerHTML = '';

  // simple tree layout: columns by depth inferred from edges
  const depthMap = new Map();
  const indeg = new Map();
  nodes.forEach(n=>indeg.set(n.id,0));
  edges.forEach(e=>indeg.set(e.to,(indeg.get(e.to)||0)+1));

  const roots = nodes.filter(n=> (indeg.get(n.id)||0) === 0);
  const byId = new Map(nodes.map(n=>[n.id,n]));

  function depthOf(id){
    if(depthMap.has(id)) return depthMap.get(id);
    const parentEdge = edges.find(e=>e.to===id);
    if(!parentEdge){
      depthMap.set(id,0);
      return 0;
    }
    const d = depthOf(parentEdge.from)+1;
    depthMap.set(id,d);
    return d;
  }

  nodes.forEach(n=>depthOf(n.id));
  const colX = d=> 20 + d*240;
  const scaleY = s=> 20 + s*80; // increased row height to avoid overlap
  const order = nodes.slice().sort((a,b)=> a.start - b.start);
  const yMap = new Map(order.map((n,i)=>[n.id, scaleY(i)]));

  // draw nodes
  for(const n of nodes){
    const el = document.createElement('div');
    el.className='node';
    el.style.left = colX(depthMap.get(n.id))+'px';
    el.style.top = (yMap.get(n.id)||0)+'px';

    const t = document.createElement('div');
    t.className='title';
    const ann = annotations[n.id]||{args:{}};
    const pidx = n.label.indexOf('(');
    const base = pidx >= 0 ? n.label.slice(0, pidx) : n.label;
    const argsInline = Object.entries(ann.args||{}).map(([k,v])=> k+': '+stringify(v)).join(', ');
    t.textContent = base + '(' + argsInline + ')';
    el.appendChild(t);

    if ('ret' in ann) {
      const kv=document.createElement('div');
      kv.className='kv';
      kv.textContent = 'return: '+stringify(ann.ret);
      el.appendChild(kv);
    }

    // Border color consistent with timeline color
    el.style.borderColor = colorFor(n.label);
    graph.appendChild(el);
  }

  // draw edges (behind nodes)
  const edgeLayer = document.createElement('div');
  edgeLayer.style.position='absolute';
  edgeLayer.style.left='0';
  edgeLayer.style.top='0';
  edgeLayer.style.right='0';
  edgeLayer.style.bottom='0';
  edgeLayer.style.zIndex='0';
  graph.appendChild(edgeLayer);

  for(const e of edges){
    const a = byId.get(e.from), b = byId.get(e.to);
    if(!a||!b) continue;

    const x1 = colX(depthMap.get(a.id)) + 220 - 4, y1 = (yMap.get(a.id)||0) + 24; // start closer to node right edge center
    const x2 = colX(depthMap.get(b.id)) + 4, y2 = (yMap.get(b.id)||0) + 24; // end slightly inside target left edge
    const dx = x2 - x1, dy = y2 - y1;
    const len=Math.hypot(dx,dy);
    const ang=Math.atan2(dy,dx)*180/Math.PI;

    const edge = document.createElement('div');
    edge.className='edge';
    edge.style.left=x1+'px';
    edge.style.top=y1+'px';
    edge.style.width=len+'px';
    edge.style.transform='rotate('+ang+'deg)';
    edgeLayer.appendChild(edge);
  }
}

function stringify(v){
  try {
    if (v===null||v===undefined) return 'null';
    if (typeof v==='object') return JSON.stringify(v);
    return String(v);
  } catch { return String(v); }
}`;
}

/**
 * Animator script
 */
function getAnimatorScript(): string {
  return `
const vscode = acquireVsCodeApi();
const play=document.getElementById('play');
const pause=document.getElementById('pause');
const stop=document.getElementById('stop');
const prev=document.getElementById('prev');
const next=document.getElementById('next');
const speed=document.getElementById('speed');
const info=document.getElementById('info');

play.onclick=()=>vscode.postMessage({type:'play'});
pause.onclick=()=>vscode.postMessage({type:'pause'});
stop.onclick=()=>vscode.postMessage({type:'stop'});
next.onclick=()=>vscode.postMessage({type:'stepNext'});
prev.onclick=()=>vscode.postMessage({type:'stepPrev'});
speed.oninput=()=>vscode.postMessage({type:'speed', ms: Number(speed.value)});

window.addEventListener('message', e=>{
  const msg=e.data;
  if(msg.type==='state'){
    speed.value=String(msg.speedMs||600);
    info.textContent = 'Step: ' + (msg.cursorSeq ?? 0);
  }
});`;
}
