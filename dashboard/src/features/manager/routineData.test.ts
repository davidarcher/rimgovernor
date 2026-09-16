import {expect, it} from 'vitest';
import {readRoutineStatus} from './routineData';
const row = (goal: string, extra: Record<string, unknown> = {}) => ({goal, score: 10, deficit: 0.5, risk: null, waitingSince: 100, selected: false, committed: false, reason: 'capacity_committed', bottleneck: '', ...extra});
const development = (extra: Record<string, unknown> = {}) => ({tick: 500, workers: 3, labor: [{work: 'Construction', free: 0}, {work: 'Research', free: 1}], capacity: 2, committed: ['player-room'], rows: [row('ensure-research', {selected: true, reason: ''}), row('ensure-comfort', {reason: 'labor_unavailable', bottleneck: 'Construction'}), row('maintain-wood', {risk: 1, reason: 'risk_deferred'}), row('maintain-resource', {deficit: null, reason: 'deficit_unknown'})], ...extra});
const status = (extra: Record<string, unknown> = {}) => ({reviewsEnabled: true, methodsEnabled: false, activeFamilies: ['routine-bill-plans'], lastReviewTick: 500, development: development(), ...extra});
it('preserves labor rows, deferral reasons, bottlenecks and unknown facts', () => {
  const result = readRoutineStatus(status());
  expect(result.development?.labor).toEqual([{work: 'Construction', free: 0}, {work: 'Research', free: 1}]);
  expect(result.development?.rows.map(r => r.reason)).toEqual(['', 'labor_unavailable', 'risk_deferred', 'deficit_unknown']);
  expect(result.development?.rows[1].bottleneck).toBe('Construction');
  expect(result.development?.rows[3].deficit).toBeNull();
  expect(readRoutineStatus(status({lastReviewTick: null, development: null}))).toMatchObject({lastReviewTick: null, development: null});
  expect(readRoutineStatus(status({development: development({workers: null, labor: [], committed: [], rows: []})})).development).toMatchObject({workers: null, capacity: 2});
});
it('rejects unknown reasons, bottlenecks without a labor deferral, and admission beyond capacity', () => {
  expect(() => readRoutineStatus(status({development: development({rows: [row('x', {reason: 'novel'})]})}))).toThrow();
  expect(() => readRoutineStatus(status({development: development({rows: [row('x', {bottleneck: 'Mining'})]})}))).toThrow();
  expect(() => readRoutineStatus(status({development: development({rows: [row('x', {selected: true, reason: ''}), row('y', {selected: true, reason: ''})]})}))).toThrow();
  expect(() => readRoutineStatus(status({development: development({rows: [row('x'), row('x')]})}))).toThrow();
  expect(() => readRoutineStatus(status({development: development({rows: [row('x', {deficit: 1.5})]})}))).toThrow();
  expect(() => readRoutineStatus(status({extra: true}))).toThrow();
});
