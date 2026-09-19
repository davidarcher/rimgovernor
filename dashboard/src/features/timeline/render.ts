// SVG rendering of a Timeline: lanes of packed spans and markers on one
// axis, wall seconds since the first row by default or ticks advanced
// (paused time collapses to nothing) when the axis mode says so, with the
// other unit read off the top ruler. Wheel zooms around the cursor, drag
// pans, hover shows the item, click pins it in the detail panel.
import {compact} from './json';
import {lanes, type Item, type LaneId, type Timeline} from './model';

export type AxisMode = 'wall' | 'tick';
export type ViewOptions = {axis: AxisMode; hidden: Set<LaneId>; launch: number | null};

const SVG = 'http://www.w3.org/2000/svg';
const GUTTER = 118;
const ROW = 18;
const RULER = 22;
const LANE_GAP = 6;
const MIN_LABEL_PX = 36;
const MARKER_LABEL_PX = 108;

export type View = {
  render(): void;
  reset(): void;
  setOptions(options: Partial<ViewOptions>): void;
  dispose(): void;
};

type Placed = {item: Item; row: number};

export function mountTimeline(host: HTMLElement, detail: HTMLElement, timeline: Timeline, initial: Partial<ViewOptions> = {}): View {
  const options: ViewOptions = {axis: 'wall', hidden: new Set(), launch: null, ...initial};
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('class', 'tl-svg');
  const tooltip = document.createElement('div');
  tooltip.className = 'tl-tooltip';
  tooltip.hidden = true;
  host.replaceChildren(svg, tooltip);

  // The view window in axis units (seconds or ticks).
  let view0 = 0;
  let view1 = 1;
  let pinned: Item | null = null;
  const full = (): [number, number] => options.axis === 'wall' ? [0, timeline.t1 - timeline.t0] : [0, Math.max(1, timeline.ticks.cumulativeAt(timeline.t1))];
  const pos = (wall: number): number => options.axis === 'wall' ? wall - timeline.t0 : timeline.ticks.cumulativeAt(wall);
  const wallAt = (p: number): number => options.axis === 'wall' ? timeline.t0 + p : (timeline.ticks.wallAtCumulative(p) ?? timeline.t0);
  const reset = () => {[view0, view1] = full(); if (view1 <= view0) view1 = view0 + 1;};
  reset();

  const width = () => Math.max(320, host.clientWidth);
  const plotWidth = () => width() - GUTTER - 8;
  const x = (p: number) => GUTTER + (p - view0) / (view1 - view0) * plotWidth();
  const unit = () => options.axis === 'wall' ? 's' : 't';

  function visible(): Item[] {
    return timeline.items.filter(item => !options.hidden.has(item.lane) && (options.launch === null || item.launch === -1 || item.launch === options.launch) && pos(item.end) >= view0 && pos(item.start) <= view1);
  }

  // pack assigns each lane's items to sub-rows so that spans (with a
  // minimum pixel footprint, so labels stay legible) do not overlap. The
  // clock lane keeps row 0 for the paused strip.
  function pack(items: Item[]): Map<LaneId, Placed[]> {
    const byLane = new Map<LaneId, Placed[]>();
    const pxPerUnit = plotWidth() / (view1 - view0);
    for (const lane of lanes) {
      const reserve = lane.id === 'clock' ? 1 : 0;
      const own = items.filter(item => item.lane === lane.id).sort((a, b) => a.start - b.start || b.end - a.end);
      const rowEnds: number[] = [];
      const placed: Placed[] = [];
      for (const item of own) {
        if (item.kind === 'paused') {placed.push({item, row: 0}); continue;}
        const start = pos(item.start);
        const marker = (pos(item.end) - start) * pxPerUnit < 2;
        const minPx = item.kind === 'stop_latency' || item.kind === 'hold' ? 0 : marker ? 10 + Math.min(MARKER_LABEL_PX, 6.5 * item.label.length) : MIN_LABEL_PX;
        const end = Math.max(pos(item.end), start + minPx / pxPerUnit);
        let row = -1;
        for (let i = reserve; i < rowEnds.length; i++) if (rowEnds[i] <= start) {row = i; break;}
        if (row < 0) row = Math.max(reserve, rowEnds.length);
        while (rowEnds.length < row) rowEnds.push(0);
        rowEnds[row] = end;
        placed.push({item, row});
      }
      byLane.set(lane.id, placed);
    }
    return byLane;
  }

  function render(): void {
    const w = width();
    const items = visible();
    const packed = pack(items);
    const laneTops = new Map<LaneId, {top: number; rows: number}>();
    let y = RULER;
    for (const lane of lanes) {
      if (options.hidden.has(lane.id)) continue;
      const placed = packed.get(lane.id) ?? [];
      const rows = Math.max(1, ...placed.map(p => p.row + 1));
      laneTops.set(lane.id, {top: y, rows});
      y += rows * ROW + LANE_GAP;
    }
    const height = y + RULER;
    svg.setAttribute('width', String(w));
    svg.setAttribute('height', String(height));
    svg.setAttribute('viewBox', `0 0 ${w} ${height}`);
    const frag = document.createDocumentFragment();

    // Lane backgrounds and labels.
    for (const lane of lanes) {
      const geometry = laneTops.get(lane.id);
      if (geometry === undefined) continue;
      const band = el('rect', {x: 0, y: geometry.top, width: w, height: geometry.rows * ROW, class: 'tl-lane'});
      frag.append(band);
      frag.append(text(6, geometry.top + 13, lane.label, 'tl-lane-label'));
    }
    frag.append(el('rect', {x: 0, y: 0, width: GUTTER - 2, height, class: 'tl-gutter'}));

    // Rulers: the axis unit at the bottom, the other unit at the top.
    const step = niceStep((view1 - view0) / Math.max(4, plotWidth() / 90));
    for (let p = Math.ceil(view0 / step) * step; p <= view1; p += step) {
      const px = x(p);
      frag.append(el('line', {x1: px, x2: px, y1: RULER, y2: height - RULER, class: 'tl-grid'}));
      frag.append(text(px + 3, height - 6, `${format(p)}${unit()}`, 'tl-ruler'));
      const wall = wallAt(p);
      const other = options.axis === 'wall' ? timeline.ticks.tickAt(wall) : wall - timeline.t0;
      if (other !== null) frag.append(text(px + 3, 14, options.axis === 'wall' ? `t${Math.round(other)}` : `${other.toFixed(1)}s`, 'tl-ruler tl-ruler-top'));
    }

    // Launch boundaries.
    for (const launch of timeline.launches) {
      if (!Number.isFinite(launch.first) || launch.index === 0) continue;
      const px = x(pos(launch.first));
      if (px < GUTTER || px > w) continue;
      frag.append(el('line', {x1: px, x2: px, y1: RULER, y2: height - RULER, class: 'tl-launch'}));
      frag.append(text(px + 3, RULER + 12, launch.name, 'tl-launch-label'));
    }

    // Items.
    const pxPerUnit = plotWidth() / (view1 - view0);
    for (const lane of lanes) {
      const geometry = laneTops.get(lane.id);
      if (geometry === undefined) continue;
      for (const {item, row} of packed.get(lane.id) ?? []) {
        const top = geometry.top + row * ROW;
        const x0 = Math.max(GUTTER, x(pos(item.start)));
        const x1 = Math.min(w, x(pos(item.end)));
        const g = el('g', {class: `tl-item kind-${item.kind.replace(/[^a-z_]/gi, '-')} sev-${item.severity}${pinned?.id === item.id ? ' pinned' : ''}`});
        g.dataset.id = item.id;
        const spanWidth = pos(item.end) - pos(item.start);
        if (spanWidth * pxPerUnit >= 2) {
          const h = item.kind === 'paused' ? 5 : item.kind === 'stop_latency' || item.kind === 'hold' ? 6 : ROW - 4;
          const yy = item.kind === 'paused' ? top + 6 : item.kind === 'stop_latency' || item.kind === 'hold' ? top + ROW / 2 - 3 : top + 2;
          g.append(el('rect', {x: x0, y: yy, width: Math.max(2, x1 - x0), height: h, rx: 2}));
          if (item.label && x1 - x0 >= MIN_LABEL_PX) {
            const clip = el('svg', {x: x0 + 3, y: top, width: Math.max(0, x1 - x0 - 6), height: ROW});
            clip.append(text(0, 13, item.label, 'tl-label'));
            g.append(clip);
          }
        } else {
          const cx = x(pos(item.start));
          const cy = top + ROW / 2;
          g.append(el('path', {d: `M${cx} ${cy - 6} L${cx + 6} ${cy} L${cx} ${cy + 6} L${cx - 6} ${cy} Z`}));
          if (item.label) {
            const clip = el('svg', {x: cx + 8, y: top, width: Math.max(0, Math.min(MARKER_LABEL_PX, w - cx - 8)), height: ROW});
            clip.append(text(0, 13, item.label, 'tl-label tl-label-marker'));
            g.append(clip);
          }
        }
        if (item.delivered !== null && item.delivered > item.end) {
          const dx = x(pos(item.delivered));
          g.append(el('line', {x1: x1, x2: dx, y1: top + ROW / 2, y2: top + ROW / 2, class: 'tl-delivery'}));
          g.append(el('line', {x1: dx, x2: dx, y1: top + 4, y2: top + ROW - 4, class: 'tl-delivery'}));
        }
        frag.append(g);
      }
    }
    svg.replaceChildren(frag);
  }

  // Interaction.
  const byId = new Map(timeline.items.map(item => [item.id, item]));
  const itemAt = (target: EventTarget | null): Item | null => {
    if (!(target instanceof Element)) return null;
    const g = target.closest<SVGGElement>('g.tl-item');
    const id = g?.dataset.id;
    return id === undefined ? null : byId.get(id) ?? null;
  };
  const onMove = (event: MouseEvent) => {
    if (dragging !== null) {
      const dx = event.clientX - dragging.x;
      const shift = -dx / plotWidth() * (view1 - view0);
      view0 = dragging.v0 + shift;
      view1 = dragging.v1 + shift;
      render();
      return;
    }
    const item = itemAt(event.target);
    if (item === null) {tooltip.hidden = true; return;}
    tooltip.hidden = false;
    tooltip.replaceChildren(tooltipContent(item));
    const rect = host.getBoundingClientRect();
    const left = Math.min(event.clientX - rect.left + 12, rect.width - 340);
    tooltip.style.left = `${Math.max(0, left)}px`;
    tooltip.style.top = `${event.clientY - rect.top + 14}px`;
  };
  const onLeave = () => {tooltip.hidden = true;};
  const onClick = (event: MouseEvent) => {
    if (moved) {moved = false; return;}
    const item = itemAt(event.target);
    pinned = item;
    detail.replaceChildren(item === null ? '' : detailContent(item));
    render();
  };
  const onWheel = (event: WheelEvent) => {
    event.preventDefault();
    const rect = svg.getBoundingClientRect();
    const px = event.clientX - rect.left;
    const p = view0 + (px - GUTTER) / plotWidth() * (view1 - view0);
    const factor = Math.exp(event.deltaY * 0.0015);
    const [f0, f1] = full();
    const span = Math.min(f1 - f0, Math.max((f1 - f0) / 20000, (view1 - view0) * factor));
    const frac = (p - view0) / (view1 - view0);
    view0 = p - frac * span;
    view1 = view0 + span;
    render();
  };
  let dragging: {x: number; v0: number; v1: number} | null = null;
  let moved = false;
  const onDown = (event: MouseEvent) => {dragging = {x: event.clientX, v0: view0, v1: view1}; moved = false;};
  const onUp = (event: MouseEvent) => {if (dragging !== null && Math.abs(event.clientX - dragging.x) > 3) moved = true; dragging = null;};
  const onDouble = () => {reset(); render();};
  svg.addEventListener('mousemove', onMove);
  svg.addEventListener('mouseleave', onLeave);
  svg.addEventListener('click', onClick);
  svg.addEventListener('wheel', onWheel, {passive: false});
  svg.addEventListener('mousedown', onDown);
  window.addEventListener('mouseup', onUp);
  svg.addEventListener('dblclick', onDouble);
  const onResize = () => render();
  window.addEventListener('resize', onResize);

  function tooltipContent(item: Item): DocumentFragment {
    const frag = document.createDocumentFragment();
    const head = document.createElement('div');
    head.className = 'tl-tooltip-title';
    head.textContent = item.title;
    const when = document.createElement('div');
    when.className = 'tl-tooltip-when';
    const tick = item.tick ?? timeline.ticks.tickAt(item.start);
    when.textContent = `${(item.start - timeline.t0).toFixed(3)}s${item.end > item.start ? ` → ${(item.end - timeline.t0).toFixed(3)}s` : ''}${tick !== null ? ` · tick ${Math.round(tick)}` : ''} · ${new Date(item.start * 1000).toISOString()}${item.delivered !== null && item.delivered > item.end ? ` · delivered +${((item.delivered - item.end) * 1000).toFixed(0)}ms` : ''}`;
    const body = document.createElement('pre');
    body.textContent = compact(item.detail, 900);
    frag.append(head, when, body);
    return frag;
  }
  function detailContent(item: Item): DocumentFragment {
    const frag = tooltipContent(item);
    const pre = frag.querySelector('pre');
    if (pre !== null) pre.textContent = compact(item.detail, 60000);
    return frag;
  }

  return {
    render,
    reset: () => {reset(); render();},
    setOptions: (next: Partial<ViewOptions>) => {const axis = options.axis; Object.assign(options, next); if (next.axis !== undefined && next.axis !== axis) reset(); render();},
    dispose: () => {window.removeEventListener('mouseup', onUp); window.removeEventListener('resize', onResize);},
  };
}

function el<K extends keyof SVGElementTagNameMap>(tag: K, attributes: Record<string, string | number>): SVGElementTagNameMap[K] {
  const node = document.createElementNS(SVG, tag);
  for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, String(value));
  return node;
}
function text(x: number, y: number, content: string, className: string): SVGTextElement {
  const node = el('text', {x, y, class: className});
  node.textContent = content;
  return node;
}
function niceStep(raw: number): number {
  const power = Math.pow(10, Math.floor(Math.log10(Math.max(raw, 1e-9))));
  const mantissa = raw / power;
  return (mantissa < 1.5 ? 1 : mantissa < 3.5 ? 2 : mantissa < 7.5 ? 5 : 10) * power;
}
function format(v: number): string {
  if (Math.abs(v) >= 100 || Number.isInteger(v)) return v.toFixed(0);
  return v.toFixed(Math.abs(v) >= 10 ? 1 : Math.abs(v) >= 1 ? 2 : 3).replace(/\.?0+$/, '');
}
