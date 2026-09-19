// Entry for timeline.html: pick (or drop) a case output directory, build
// the timeline and mount the view with its toolbar, summary and detail
// panel. Everything runs in the browser over the picked files; nothing is
// uploaded or fetched.
import './timeline.css';
import {readCase, type NamedFile} from './files';
import {buildTimeline, lanes, type LaneId, type Timeline} from './model';
import {mountTimeline, type AxisMode, type View} from './render';

const byId = <T extends HTMLElement>(id: string): T => {
  const node = document.getElementById(id);
  if (node === null) throw Error(`timeline page is missing #${id}`);
  return node as T;
};

const picker = byId<HTMLInputElement>('tl-pick');
const drop = byId<HTMLElement>('tl-drop');
const status = byId<HTMLElement>('tl-status');
const summary = byId<HTMLElement>('tl-summary');
const notes = byId<HTMLElement>('tl-notes');
const toolbar = byId<HTMLElement>('tl-toolbar');
const plot = byId<HTMLElement>('tl-plot');
const detail = byId<HTMLElement>('tl-detail');
const unplacedList = byId<HTMLElement>('tl-unplaced');

let view: View | null = null;

function namedFiles(list: FileList | File[]): NamedFile[] {
  return [...list].map(file => ({path: (file.webkitRelativePath || file.name).replace(/\\/g, '/'), text: () => file.text()}));
}

async function load(files: NamedFile[]): Promise<void> {
  status.textContent = `Reading ${files.length} files…`;
  try {
    const started = performance.now();
    const read = await readCase(files);
    const timeline = buildTimeline(read.files);
    show(timeline, read.root, read.skipped);
    status.textContent = `${read.root || 'case'}: ${timeline.items.length} items over ${(timeline.t1 - timeline.t0).toFixed(1)}s in ${(performance.now() - started).toFixed(0)}ms`;
  } catch (reason) {
    status.textContent = reason instanceof Error ? reason.message : 'Could not read the case directory';
  }
}

function show(timeline: Timeline, root: string, skipped: string[]): void {
  view?.dispose();
  summary.replaceChildren(...timeline.summary.map(field => {
    const chip = document.createElement('span');
    chip.className = 'tl-chip';
    const label = document.createElement('b');
    label.textContent = field.label;
    chip.append(label, ` ${field.value}`);
    return chip;
  }));
  if (timeline.error !== null) {
    const chip = document.createElement('span');
    chip.className = 'tl-chip tl-chip-error';
    chip.textContent = timeline.error;
    summary.append(chip);
  }
  const noteLines = [...timeline.notes, ...skipped, ...timeline.launches.flatMap(l => l.gaps.map(g => `${l.name}: ${g.reason} at line ${g.line}`))];
  notes.replaceChildren(...noteLines.map(line => {const li = document.createElement('li'); li.textContent = line; return li;}));
  notes.hidden = noteLines.length === 0;
  unplacedList.replaceChildren(...timeline.unplaced.map(entry => {
    const li = document.createElement('li');
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = entry.name;
    button.addEventListener('click', () => {
      const pre = document.createElement('pre');
      pre.textContent = JSON.stringify(entry.detail, null, 1) ?? '';
      const head = document.createElement('div');
      head.className = 'tl-tooltip-title';
      head.textContent = `${entry.name} (unstamped; not on the axis)`;
      detail.replaceChildren(head, pre);
    });
    li.append(button);
    return li;
  }));
  byId<HTMLElement>('tl-unplaced-box').hidden = timeline.unplaced.length === 0;
  document.title = `${root || 'case'} · RimGovernor case timeline`;

  const hidden = new Set<LaneId>();
  toolbar.replaceChildren();
  const axis = document.createElement('select');
  for (const [value, label] of [['wall', 'wall seconds'], ['tick', 'ticks advanced']] as const) {
    const option = document.createElement('option');
    option.value = value;
    option.textContent = label;
    axis.append(option);
  }
  axis.addEventListener('change', () => view?.setOptions({axis: axis.value === 'tick' ? 'tick' : 'wall' satisfies AxisMode}));
  toolbar.append(labelled('Axis', axis));
  if (timeline.launches.length > 1) {
    const launch = document.createElement('select');
    const all = document.createElement('option');
    all.value = '';
    all.textContent = 'all launches';
    launch.append(all);
    for (const l of timeline.launches) {const option = document.createElement('option'); option.value = String(l.index); option.textContent = l.name; launch.append(option);}
    launch.addEventListener('change', () => view?.setOptions({launch: launch.value === '' ? null : Number(launch.value)}));
    toolbar.append(labelled('Launch', launch));
  }
  for (const lane of lanes) {
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.checked = true;
    box.addEventListener('change', () => {if (box.checked) hidden.delete(lane.id); else hidden.add(lane.id); view?.setOptions({hidden});});
    toolbar.append(labelled(lane.label, box));
  }
  const reset = document.createElement('button');
  reset.type = 'button';
  reset.textContent = 'Reset zoom';
  reset.addEventListener('click', () => view?.reset());
  toolbar.append(reset);
  const hint = document.createElement('span');
  hint.className = 'tl-hint';
  hint.textContent = 'wheel: zoom · drag: pan · click: pin · double-click: reset';
  toolbar.append(hint);
  detail.replaceChildren();
  view = mountTimeline(plot, detail, timeline, {hidden});
  view.render();
}

function labelled(text: string, control: HTMLElement): HTMLLabelElement {
  const label = document.createElement('label');
  label.className = 'tl-control';
  label.append(control, ` ${text}`);
  return label;
}

picker.addEventListener('change', () => {if (picker.files !== null && picker.files.length > 0) void load(namedFiles(picker.files));});
drop.addEventListener('dragover', event => {event.preventDefault(); drop.classList.add('over');});
drop.addEventListener('dragleave', () => drop.classList.remove('over'));
drop.addEventListener('drop', event => {
  event.preventDefault();
  drop.classList.remove('over');
  const transfer = event.dataTransfer;
  if (transfer === null) return;
  void collectDrop(transfer).then(files => {if (files.length > 0) void load(files);});
});

// collectDrop walks dropped directory entries (webkitGetAsEntry) so a
// dropped case directory yields the same relative paths as the picker.
async function collectDrop(transfer: DataTransfer): Promise<NamedFile[]> {
  const out: NamedFile[] = [];
  const walk = async (entry: FileSystemEntry, prefix: string): Promise<void> => {
    if (entry.isFile) {
      const file = await new Promise<File>((resolve, reject) => (entry as FileSystemFileEntry).file(resolve, reject));
      out.push({path: prefix + entry.name, text: () => file.text()});
      return;
    }
    if (!entry.isDirectory) return;
    const reader = (entry as FileSystemDirectoryEntry).createReader();
    for (;;) {
      const batch = await new Promise<FileSystemEntry[]>((resolve, reject) => reader.readEntries(resolve, reject));
      if (batch.length === 0) break;
      for (const child of batch) await walk(child, `${prefix}${entry.name}/`);
    }
  };
  for (const item of transfer.items) {
    const entry = item.webkitGetAsEntry();
    if (entry !== null) await walk(entry, '');
  }
  if (out.length === 0) out.push(...namedFiles(transfer.files));
  return out;
}
