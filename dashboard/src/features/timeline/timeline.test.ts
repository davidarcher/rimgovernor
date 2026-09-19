import {describe, expect, it} from 'vitest';
import {caseRoot, readCase} from './files';
import {findEvents, parseFlight, replyClock, segmentOrder} from './flight';
import {recording, result, stderrLog, T0, toolReply} from './fixture';
import {buildTickMap, buildTimeline, type CaseFiles, type Item} from './model';
import {goDurationMs, parseStderr} from './stderr';

describe('flight rows', () => {
  it('orders rotated segments oldest first and the active file last', () => {
    expect(segmentOrder('flight.jsonl', ['flight.jsonl', 'flight.jsonl.1', 'flight.jsonl.10', 'flight.jsonl.2', 'other.jsonl', 'flight.jsonl.bak'])).toEqual(['flight.jsonl.10', 'flight.jsonl.2', 'flight.jsonl.1', 'flight.jsonl']);
  });
  it('keeps well-formed rows and records gaps for corrupt lines and sequence jumps', () => {
    const lines = [
      JSON.stringify({sequence: 1, wall_time: 1, kind: 'coverage', context: {}, payload: {}}),
      '{not json',
      JSON.stringify({sequence: 2, wall_time: 2, kind: 'clock_step', context: {}, payload: {reads: 1}}),
      JSON.stringify({sequence: 5, wall_time: 3, kind: 'clock_step', context: {}, payload: {}}),
      JSON.stringify({sequence: 6, wall_time: 4, kind: 'x', payload: {}}),
    ].join('\n');
    const flight = parseFlight([lines]);
    expect(flight.rows.map(r => r.sequence)).toEqual([1, 2, 5]);
    expect(flight.gaps.map(g => g.reason)).toEqual(['Incomplete or corrupt record', 'Retention or sequence discontinuity', 'Incomplete or corrupt record']);
    expect(flight.gaps[1]).toMatchObject({before: 5, after: 2});
  });
  it('reads the tick and paused state the way the Go phase reader does', () => {
    expect(replyClock({status: {context: {tick: '42'}, actualPaused: true}})).toEqual({tick: 42, paused: true});
    expect(replyClock({observed: {context: {tick: 7}, clockStatus: {running: {}}}})).toEqual({tick: 7, paused: false});
    expect(replyClock({receipt: {applied: {status: {context: {tick: '9'}, stopped: {}}}}})).toEqual({tick: null, paused: true});
    expect(replyClock({failure: {detail: 'x'}})).toEqual({tick: null, paused: null});
  });
  it('finds events pages anywhere in a reply in document order', () => {
    const events = findEvents({observed: {events: {events: [{cursor: '1'}, {cursor: '2'}]}}, page: {events: [{cursor: '3'}]}});
    expect(events.map(e => e.cursor)).toEqual(['1', '2', '3']);
  });
});

describe('stderr step blocks', () => {
  it('splits the scheduler log into one block per step reads line and reads its fields', () => {
    const log = parseStderr(stderrLog);
    expect(log.steps).toHaveLength(2);
    const [first, second] = log.steps;
    expect(first).toMatchObject({tick: 11, running: false, stopped: false, reason: 'full', admitted: true, refused: [], mode: 'colony', gateWaitMs: 56, elapsedMs: 800, error: null, deferred: false, heldMs: null});
    expect(first.planners).toEqual([{planner: 'Haul', reason: 'admitted', plan: 'routine-haul-1'}]);
    expect(first.window).toEqual({ticks: 2500, targetSeconds: 2, ticksPerSecond: 360});
    expect(second).toMatchObject({tick: 735, stopped: true, stopReason: 'STOP_REASON_WATCH_LATCHED', reason: 'wake', admitted: false, refused: ['no_work'], mode: null, heldMs: 114, elapsedMs: 1500, error: 'building execution held'});
    expect(second.lines.at(-1)).toBe('[clock-worker] step failed: building execution held');
    // The worker line after the closers opens the next (unfinished) block.
    expect(log.trailing).toHaveLength(1);
    expect(log.trailing[0]).toContain('[worker] routine-haul-1-0');
  });
  it('closes the last block at end of input and keeps unfinished lines as trailing', () => {
    const log = parseStderr('[clock-scheduler] status: x\n[clock-scheduler] step reads: total=1 elapsed=3ms\n[clock-scheduler] status: y\n');
    expect(log.steps).toHaveLength(1);
    expect(log.trailing).toEqual(['[clock-scheduler] status: y']);
  });
  it('reads Go durations', () => {
    expect(goDurationMs('942ms')).toBe(942);
    expect(goDurationMs('1.915s')).toBeCloseTo(1915);
    expect(goDurationMs('1m2.5s')).toBeCloseTo(62500);
    expect(goDurationMs('12')).toBeNull();
  });
});

describe('tick map', () => {
  it('interpolates ticks between samples and collapses paused time on the cumulative axis', () => {
    const map = buildTickMap([{wall: 10, tick: 100}, {wall: 20, tick: 200}, {wall: 30, tick: 200}, {wall: 40, tick: 50}, {wall: 50, tick: 150}]);
    expect(map.tickAt(15)).toBe(150);
    expect(map.tickAt(25)).toBe(200);
    expect(map.tickAt(5)).toBe(100);
    expect(map.tickAt(60)).toBe(150);
    expect(map.resets).toBe(1);
    expect(map.ticksAdvanced).toBe(200);
    expect(map.cumulativeAt(30)).toBe(100);
    expect(map.cumulativeAt(45)).toBe(150);
    expect(map.wallAtCumulative(50)).toBe(15);
    expect(map.wallAtCumulative(150)).toBe(45);
  });
});

function caseFiles(): CaseFiles {
  const flight = parseFlight([recording()]);
  return {result, launches: [{name: 'service', flight, stderr: stderrLog, http: [{name: 'http-0001.json', body: {method: 'GET', path: '/api/health', status: 200}}]}], evidence: [{name: '0001-prepare.json', body: {ok: true}}, {name: '0002-stamped.json', body: {observed_at: new Date((T0 + 2.2) * 1000).toISOString(), ok: true}}]};
}
const kinds = (items: Item[], lane: string) => items.filter(i => i.lane === lane).map(i => i.kind);

describe('timeline model', () => {
  const timeline = buildTimeline(caseFiles());
  const at = (item: Item) => Number((item.start - T0).toFixed(3));

  it('spans the case from started_at to finished_at', () => {
    expect(timeline.t0).toBe(T0 - 5);
    expect(timeline.t1).toBe(T0 + 6);
    expect(kinds(timeline.items, 'harness')).toEqual(['case', 'boot', 'evidence', 'case']);
    expect(timeline.unplaced.map(u => u.name)).toEqual(['service/http-0001.json', '0001-prepare.json']);
  });
  it('draws the window from the started event to the stopped event at the native stamps', () => {
    const windows = timeline.items.filter(i => i.kind === 'window');
    expect(windows).toHaveLength(1);
    expect(at(windows[0])).toBe(1.2);
    expect(Number((windows[0].end - T0).toFixed(3))).toBe(3);
    expect(windows[0].label).toBe('724/2500t · watch_latched');
    expect(windows[0].severity).toBe('ok');
    const stop = timeline.items.find(i => i.kind === 'stop');
    expect(stop?.delivered).toBeCloseTo(T0 + 3.15);
    // Paused from the first bundle (t+0.15, paused) to the start receipt
    // (t+1.2, running); the last sample (t+3.15, paused) has no successor.
    expect(kinds(timeline.items, 'clock')).toEqual(['paused', 'window', 'stop']);
  });
  it('places steps by elapsed time, with the stop latency before a woken step', () => {
    const steps = timeline.items.filter(i => i.kind === 'step');
    expect(steps.map(at)).toEqual([0.1, 3.2]);
    expect(steps[0].label).toBe('full · 2500t');
    expect(steps[1].severity).toBe('error');
    expect(steps[1].title).toContain('building execution held');
    const latency = timeline.items.find(i => i.kind === 'stop_latency');
    expect(latency !== undefined && at(latency)).toBe(3);
  });
  it('shows admissions from clock control replies and refusals from the aligned stderr step', () => {
    const admissions = timeline.items.filter(i => i.lane === 'admissions');
    expect(admissions.map(i => [i.kind, i.label])).toEqual([
      ['admit', 'start superfast'],
      ['hold', ''],
      ['refuse', 'no_work'],
      ['planner', 'Haul: no_active_deficit'],
      ['dispatch', 'routine-haul accepted'],
      ['refuse', 'renew'],
    ]);
    expect(admissions[2].severity).toBe('info');
    expect(admissions[5].severity).toBe('error');
    expect(at(admissions[1])).toBe(3.086);
  });
  it('places native events at the companion stamp with the delivery latency', () => {
    const events = timeline.items.filter(i => i.lane === 'events');
    expect(events).toHaveLength(1);
    expect(events[0]).toMatchObject({kind: 'event:alert', label: 'alert NeedFood', severity: 'warn'});
    expect(at(events[0])).toBe(2.5);
    expect(events[0].delivered).toBeCloseTo(T0 + 3.15);
  });
  it('spans native calls from request to response, marking long polls and errors', () => {
    const calls = timeline.items.filter(i => i.lane === 'calls');
    expect(calls.map(i => i.kind)).toEqual(['call', 'call', 'poll', 'call', 'call', 'call', 'error']);
    expect(calls[2].title).toContain('long poll');
    expect(calls[6].title).toBe('lifecycle_read_tick 100ms error');
  });
  it('summarizes the run', () => {
    const summary = Object.fromEntries(timeline.summary.map(f => [f.label, f.value]));
    expect(summary).toMatchObject({case: 'routinehaul/storage', outcome: 'passed', ticks: '724', steps: '2 (full=1 wake=1)', admissions: '1 admitted, 2 refused', dispatches: '1 (0 refused)', 'native calls': '7 (1 errors)', events: '1'});
    expect(summary.stops).toBe('1; stop→step latency mean 200ms max 200ms over 1');
    expect(summary.paused).toBe('35% of 3.0s sampled');
    expect(timeline.notes).toEqual([]);
  });
  it('does not place stderr refusals when the blocks do not align with the rows', () => {
    const files = caseFiles();
    files.launches[0].stderr = stderrLog + '[clock-scheduler] step reads: total=1 elapsed=1ms\n';
    const misaligned = buildTimeline(files);
    expect(misaligned.notes).toEqual(['service: 3 stderr step blocks vs 2 clock_step rows; refusals from stderr are not placed.']);
    expect(kinds(misaligned.items, 'admissions')).toEqual(['admit', 'dispatch', 'refuse']);
  });
  it('closes a window left open at the end of the recording', () => {
    const files = caseFiles();
    const rows = recording().split('\n').filter(Boolean);
    const open = rows.map(line => JSON.parse(line) as {sequence: number; payload: {result?: {payload?: string}}}).filter(r => r.sequence !== 12);
    files.launches[0].flight = parseFlight([open.map(r => JSON.stringify(r)).join('\n')]);
    files.launches[0].stderr = null;
    const items = buildTimeline(files).items;
    expect(items.find(i => i.kind === 'window')?.label).toBe('?/2500t · open at end');
  });
  it('flags a request that never got a response', () => {
    const rows = [
      JSON.stringify({sequence: 1, wall_time: T0, kind: 'native_request', context: {}, payload: {tool: 'games_call_tool', arguments: {gameId: 'g', tool: 'rimgovernor/clock_pause', arguments: {request: '{}'}}}}),
      JSON.stringify({sequence: 2, wall_time: T0 + 2, kind: 'clock_step', context: {}, payload: {reads: 0, elapsed_ms: 10, reason: 'timer'}}),
    ].join('\n');
    const items = buildTimeline({result: null, launches: [{name: 'service', flight: parseFlight([rows]), stderr: null, http: []}], evidence: []}).items;
    expect(items.filter(i => i.lane === 'calls').map(i => i.title)).toEqual(['clock_pause never answered']);
  });
  it('reads a control receipt paused state from its applied status', () => {
    expect(replyClock(JSON.parse(toolReply({receipt: {applied: {status: {context: {tick: '3'}, running: {}}}}}).payload as string) as Record<string, unknown>)).toEqual({tick: null, paused: false});
  });
});

describe('case directory reading', () => {
  it('finds the case root under a picked run directory', () => {
    expect(caseRoot(['run/haul1/routinehaul/storage/result.json', 'run/haul1/routinehaul/storage/flight.jsonl', 'run/haul1.out'])).toBe('run/haul1/routinehaul/storage');
    expect(caseRoot(['storage/result.json', 'storage/service-2/flight.jsonl'])).toBe('storage');
    expect(caseRoot(['a.txt'])).toBe('');
  });
  it('maps the root recording to service and service-N recordings to their launches', async () => {
    const text = (s: string) => () => Promise.resolve(s);
    const read = await readCase([
      {path: 'case/result.json', text: text(JSON.stringify(result))},
      {path: 'case/flight.jsonl', text: text(recording())},
      {path: 'case/service/stderr.log', text: text(stderrLog)},
      {path: 'case/service/http-0002.json', text: text('{"method":"GET"}')},
      {path: 'case/service/http-0001.json', text: text('{"method":"POST"}')},
      {path: 'case/service-2/flight.jsonl.1', text: text('')},
      {path: 'case/service-2/flight.jsonl', text: text(JSON.stringify({sequence: 1, wall_time: T0 + 10, kind: 'coverage', context: {}, payload: {}}))},
      {path: 'case/service-2/stderr.log', text: text('')},
      {path: 'case/service-profile/Config/Prefs.xml', text: text('<x/>')},
      {path: 'case/0001-prepare.json', text: text('{}')},
      {path: 'case/0002-bad.json', text: text('{')},
    ]);
    expect(read.root).toBe('case');
    expect(read.files.launches.map(l => [l.name, l.flight.rows.length, l.http.map(h => h.name)])).toEqual([['service', 18, ['http-0001.json', 'http-0002.json']], ['service-2', 1, []]]);
    expect(read.files.launches[1].flight.gaps).toEqual([]);
    expect(read.files.evidence.map(e => e.name)).toEqual(['0001-prepare.json', '0002-bad.json']);
    expect(read.skipped).toEqual(['0002-bad.json: not JSON']);
  });
});
