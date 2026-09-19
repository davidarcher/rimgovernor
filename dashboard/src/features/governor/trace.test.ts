import {describe, expect, it} from 'vitest';
import {readTelemetryEvent, readTelemetryMetrics, readTelemetryPage, type TelemetryEvent} from './telemetryData';
import {buildTrace} from './trace';
import {mergeEvents} from './useTelemetry';
import {eventSummary} from './EventFeed';
import {stepRows, T, untraced} from './fixtures';

describe('telemetry readers', () => {
  it('types an events page and keeps context and payload free-form', () => {
    const page = readTelemetryPage({events: [untraced, ...stepRows, {kind: 'recording_gap', sequence: null, wall_time: 1001, reason: 'rotated', before: 3, after: 9}], next_since: 17, last_sequence: 17, more: false});
    expect(page.events).toHaveLength(10);
    expect(page.events[1].context.trace_id).toBe(T);
    expect(page.events[9]).toMatchObject({kind: 'recording_gap', sequence: null, reason: 'rotated', before: 3, after: 9});
    expect(page.events[5].text).toContain('routine-acquire-1');
    expect(() => readTelemetryPage({events: [{kind: 'x', wall_time: 1}], next_since: 0, last_sequence: 0, more: false})).toThrow('without a sequence');
    expect(() => readTelemetryEvent({sequence: 1, wall_time: 'now', kind: 'x'})).toThrow('wall time');
  });
  it('types the metrics block with a null tick and authority', () => {
    const m = readTelemetryMetrics({tick: null, tps: 0, authority: null, last_step_ms: 12.5, run: 'run-1', metrics: {native_calls: 3, native_errors: 1}});
    expect(m).toMatchObject({tick: null, authority: null, lastStepMs: 12.5, metrics: {native_calls: 3}});
    const live = readTelemetryMetrics({tick: 29900, tps: 55.5, authority: {colony: 'c', map: 0, load: 'l', plan: 'plan-a', revision: '4', native: '9'}, last_step_ms: 0, metrics: {}});
    expect(live.authority?.plan).toBe('plan-a');
    expect(() => readTelemetryMetrics({tick: 1, tps: 'fast', authority: null, last_step_ms: 0, metrics: {}})).toThrow('tps');
  });
});

describe('buildTrace', () => {
  const events = readTelemetryPage({events: [untraced, ...stepRows], next_since: 17, last_sequence: 17, more: false}).events;
  it('folds each native call into one line with its phases and nests the dispatch span', () => {
    const trace = buildTrace([...events].reverse(), T);
    expect(trace).not.toBeNull();
    if (!trace) return;
    expect(trace.root).toBe('scheduler_step: step done');
    expect(trace.tick).toBe(29900);
    expect(trace.spanMs).toBeCloseTo(200, 3);
    expect(trace.rows.map(r => r.sequence)).toEqual([10, 11, 12, 13, 14, 15, 16, 17]);
    expect(trace.lines.map(l => [l.sequence, l.kind, l.depth, l.text])).toEqual([
      [10, 'native_cache_hit', 0, 'cache hit rimgovernor/lifecycle_read_tick'],
      [11, 'native_request', 0, 'native rimgovernor/observations_read_bundle'],
      [14, 'worker_dispatch', 1, 'worker_dispatch'],
      [15, 'native_error', 1, 'native rimgovernor/operations_execute'],
      [17, 'scheduler_step', 0, 'scheduler_step "step done"'],
    ]);
    const read = trace.lines[1];
    expect(read.offsetMs).toBeCloseTo(5, 3);
    expect(read.durationMs).toBe(100);
    expect(read.phases).toEqual({gateWaitMs: 10, callMs: 80, decodeMs: 5, totalMs: 100, nativeQueueMs: 20, nativeExecuteMs: 50, echoed: ''});
    const refused = trace.lines[3];
    expect(refused.error).toBe('refused: stale_facts');
    expect(refused.phases?.nativeQueueMs).toBeNull();
    expect(trace.lines[2].attrs).toEqual([['action', 'routine-acquire-1'], ['reads', '1'], ['receipt', 'refused']]);
    expect(trace.lines[4].attrs.map(([k]) => k)).toEqual(['admitted', 'cause', 'window_ticks']);
  });
  it('returns null for an unknown or empty trace id', () => {
    expect(buildTrace(events, '')).toBeNull();
    expect(buildTrace(events, 'nope')).toBeNull();
  });
});

describe('feed helpers', () => {
  it('merges a page ahead of the buffer without duplicates, newest first', () => {
    const events = readTelemetryPage({events: stepRows, next_since: 17, last_sequence: 17, more: false}).events;
    const buffer = mergeEvents([], events.slice(0, 4));
    expect(buffer.map(e => e.sequence)).toEqual([13, 12, 11, 10]);
    const merged = mergeEvents(buffer, events.slice(2));
    expect(merged.map(e => e.sequence)).toEqual([17, 16, 15, 14, 13, 12, 11, 10]);
  });
  it('summarises kinded rows by message and named attributes, native rows by tool', () => {
    const events = readTelemetryPage({events: stepRows, next_since: 17, last_sequence: 17, more: false}).events;
    const by = (sequence: number): TelemetryEvent => events.find(e => e.sequence === sequence) as TelemetryEvent;
    expect(eventSummary(by(17))).toBe('step done cause=live window_ticks=250');
    expect(eventSummary(by(14))).toBe('action=routine-acquire-1 receipt=refused reads=1');
    expect(eventSummary(by(16))).toBe('rimgovernor/operations_execute — refused: stale_facts');
  });
});
