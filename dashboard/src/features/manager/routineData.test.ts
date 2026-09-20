import {expect, it} from 'vitest';
import {readRoutineStatus} from './routineData';
const row = (goal: string, extra: Record<string, unknown> = {}) => ({goal, score: 10, deficit: 0.5, risk: null, waitingSince: 100, selected: false, committed: false, reason: 'capacity_committed', bottleneck: '', ...extra});
const development = (extra: Record<string, unknown> = {}) => ({tick: 500, workers: 3, labor: [{work: 'Construction', free: 0}, {work: 'Research', free: 1}], capacity: 2, committed: ['player-room'], rows: [row('ensure-research', {selected: true, reason: ''}), row('ensure-comfort', {reason: 'labor_unavailable', bottleneck: 'Construction'}), row('maintain-wood', {risk: 1, reason: 'risk_deferred'}), row('maintain-resource', {deficit: null, reason: 'deficit_unknown'})], ...extra});
const profile = (extra: Record<string, unknown> = {}) => ({pawn: 'a', age: 30.5, child: false, ranged: true, traits: [{name: 'Pyromaniac', degree: 0}], effects: {workSpeed: 0, learnRate: 0.75, moveSpeed: -0.2, sociable: -1, chemicalInterest: 0, flags: ['NoFirefighting']}, skills: [{name: 'Mining', level: 12, stored: 12, passion: 'Major', disabled: false, learnFactor: 2.625}, {name: 'Art', level: 0, stored: 0, passion: '', disabled: true, learnFactor: 0}], incapable: ['Hauling'], forbidden: ['Firefighter', 'Warden'], ...extra});
const roster = (extra: Record<string, unknown> = {}) => ({tick: 500, coverage: [{work: 'Doctor', demand: 1, owners: 0, capable: 0}], decaying: [{pawn: 'a', skill: 'Mining', level: 12}], pawns: [profile()], ...extra});
const section = {section: 'colony_facts', family: 'routine', asOf: 480, complete: true, source: 'rimgovernor/colony_facts', storedAt: '2026-09-19T00:00:00Z'};
const status = (extra: Record<string, unknown> = {}) => ({reviewsEnabled: true, methodsEnabled: false, resourceRunways: [], activeFamilies: ['routine-bill-plans'], lastReviewTick: 500, development: development(), roster: roster(), sections: [section, {...section, section: 'cells', stale: {all: true}}], ...extra});
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
it('reads goal progress records with their five fields and cooldowns', () => {
  const record = {goal: 'EnsureFoodSupply', method: 'acquire', expected: 'food runway', lastProgress: 100, nextReview: 60100, blocked: 'prerequisite:EnsureCooking', cooldowns: [{key: 'harvest/Plant_Berry1', until: 900}]};
  expect(readRoutineStatus(status()).progress).toEqual([]);
  expect(readRoutineStatus(status({progress: [record]})).progress).toEqual([record]);
  expect(() => readRoutineStatus(status({progress: [{...record, nextReview: 99}]}))).toThrow();
  expect(() => readRoutineStatus(status({progress: [record, record]}))).toThrow();
  expect(() => readRoutineStatus(status({progress: [{...record, extra: true}]}))).toThrow();
  expect(() => readRoutineStatus(status({progress: [{...record, method: ''}]}))).toThrow();
});
it('reads the colony stage and rejects an inconsistent one', () => {
  const stage = {stage: 'Reserves', since: 400, blocker: 'settling', reason: 'production clear 0.5 of 2.0 days', held: false};
  expect(readRoutineStatus(status()).stage).toBeNull();
  expect(readRoutineStatus(status({stage: null})).stage).toBeNull();
  expect(readRoutineStatus(status({stage})).stage).toEqual(stage);
  expect(readRoutineStatus(status({stage: {...stage, stage: 'Development', blocker: '', reason: ''}})).stage?.stage).toBe('Development');
  expect(() => readRoutineStatus(status({stage: {...stage, stage: 'Thriving'}}))).toThrow();
  expect(() => readRoutineStatus(status({stage: {...stage, held: true}}))).toThrow();
  expect(() => readRoutineStatus(status({stage: {...stage, blocker: ''}}))).toThrow();
  expect(() => readRoutineStatus(status({stage: {...stage, extra: true}}))).toThrow();
});
it('reads the work roster report and the held sections', () => {
  const result = readRoutineStatus(status());
  expect(result.roster).toEqual(roster());
  expect(result.sections.map(s => s.section)).toEqual(['colony_facts', 'cells']);
  expect(readRoutineStatus(status({roster: null, sections: []}))).toMatchObject({roster: null, sections: []});
});
it('rejects malformed roster reports', () => {
  expect(() => readRoutineStatus(status({roster: roster({pawns: [profile(), profile()]})}))).toThrow();
  expect(() => readRoutineStatus(status({roster: roster({coverage: [{work: 'Doctor', demand: 1, owners: 0, capable: 0}, {work: 'Doctor', demand: 1, owners: 0, capable: 0}]})}))).toThrow();
  expect(() => readRoutineStatus(status({roster: roster({pawns: [profile({skills: [{name: 'Art', level: 0, stored: 0, passion: '', disabled: true, learnFactor: 1}]})]})}))).toThrow();
  expect(() => readRoutineStatus(status({roster: roster({pawns: [profile({skills: [{name: 'Art', level: 21, stored: 0, passion: '', disabled: false, learnFactor: 1}]})]})}))).toThrow();
  expect(() => readRoutineStatus(status({roster: roster({pawns: [profile({effects: {workSpeed: 0, learnRate: 0.75, moveSpeed: 0, sociable: 0, chemicalInterest: 0}})]})}))).toThrow();
  expect(() => readRoutineStatus(status({roster: roster({tick: -1})}))).toThrow();
  expect(() => readRoutineStatus(status({sections: [{...section, extra: true}]}))).toThrow();
});

const runway = {resource: 'Steel', tick: 60000, windowDays: 15, thresholdDays: 5, reserve: 20, stock: 40, surfaceOre: null, consumptionPerDay: 20, stockDays: 1, daysLeft: null, deficit: null, target: 0};
it('preserves resource runway numbers, nulls and zero rates', () => {
  expect(readRoutineStatus(status({resourceRunways: [runway]})).resourceRunways).toEqual([runway]);
  const zero = {...runway, consumptionPerDay: 0, stockDays: null, deficit: false};
  expect(readRoutineStatus(status({resourceRunways: [zero]})).resourceRunways).toEqual([zero]);
});
it('rejects malformed resource runways at the API boundary', () => {
  for (const extra of [{daysLeft: -1}, {consumptionPerDay: Infinity}, {stock: '40'}, {deficit: 0}, {tick: 0.5}, {surfaceOre: undefined}, {extra: true}]) {
    expect(() => readRoutineStatus(status({resourceRunways: [{...runway, ...extra}]}))).toThrow();
  }
  expect(() => readRoutineStatus(status({resourceRunways: null}))).toThrow();
});it('reads the colony grid, tolerates loot holds and rejects an inconsistent grid', () => {
  const grid = {origin: {x: 40, z: 50}, pitch: 16, axes: [{x: 0, z: -1}, {x: 1, z: 0}], source: 'starter_shell', bounds: {width: 250, height: 200}};
  expect(readRoutineStatus(status({colonyGrid: grid, lootHolds: [{thing: 'Thing_1', definition: 'Steel', x: 1, z: 2, reason: 'reach'}]})).colonyGrid).toEqual(grid);
  expect(readRoutineStatus(status({colonyGrid: null})).colonyGrid).toBeNull();
  expect(readRoutineStatus(status()).colonyGrid).toBeNull();
  for (const bad of [{...grid, pitch: 0}, {...grid, axes: [{x: 1, z: 0}, {x: 1, z: 0}]}, {...grid, axes: [{x: 2, z: 0}, {x: 0, z: 1}]}, {...grid, source: ''}, {...grid, origin: {x: 1}}]) {
    expect(() => readRoutineStatus(status({colonyGrid: bad}))).toThrow();
  }
});
