// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import {act, cleanup, render, screen, within} from '@testing-library/react';
import {afterEach, expect, it, vi} from 'vitest';
import PresentationPanel from './PresentationPanel';
import type {ObservationState} from './observationData';
const observation: ObservationState = {sessionId: 'session', connected: true, mode: 'manual', status: {label: ''}, identity: {colonyId: 'colony', mapId: 0, loadToken: 'load'}, game: {tick: 1, paused: true, observedAt: null, stale: false}, activePlanId: null};
const context = {identity: observation.identity, tick: '9007199254740993', nativeGeneration: '2'};
const listing = {totalCount: 0, returnedCount: 0, complete: true};
const values = {camera: {camera: {context, mapPosition: {x: 12}, rootSize: 20}}, selection: {selection: {context, selectedObjects: [{label: 'Granite wall', inspectText: 'Damaged'}], listing: {totalCount: 2, returnedCount: 1, complete: false, truncated: true}}}, colonists: {roster: {context, listing}}};
const reply = (value: unknown) => ({ok: true, text: async () => JSON.stringify(value)});
const routines = (roster: unknown = null) => ({reviewsEnabled: true, methodsEnabled: true, activeFamilies: [], lastReviewTick: 500, development: null, roster, sections: []});
function response(url: string) {if (url.endsWith('/camera')) return reply(values.camera); if (url.endsWith('/selection')) return reply(values.selection); if (url.endsWith('/routines')) return reply(routines()); return reply(values.colonists);}
afterEach(() => {cleanup(); vi.useRealTimers(); vi.unstubAllGlobals();});
it('reads three fixed GETs and renders unknown/partial/known empty facts without controls', async () => {
 const fetcher = vi.fn(async (url: string) => response(url)); vi.stubGlobal('fetch', fetcher);
 await act(async () => {render(<PresentationPanel observation={observation} observationFresh/>);});
 expect(screen.getByText('Position: 12, unknown')).toBeVisible(); expect(screen.getByText('Zoom: unknown · Root size: 20')).toBeVisible();
 expect(screen.getByText('Granite wall')).toBeVisible(); expect(screen.getByText(/Damaged/)).toBeVisible(); expect(screen.getByText(/Truncated/)).toBeVisible(); expect(screen.getByText('No colonists on this map.')).toBeVisible(); expect(screen.queryByRole('button')).toBeNull();
 expect(fetcher.mock.calls.map(v => v[0])).toEqual(['/api/presentation/camera', '/api/presentation/selection', '/api/presentation/colonists', '/api/routines']);
});
it('retains last good endpoint data on error, marks disconnect stale and recovers', async () => {
 vi.useFakeTimers(); let fail = false; vi.stubGlobal('fetch', async (url: string, options: RequestInit) => {expect(options.method).toBe('GET'); expect(options.body).toBeUndefined(); if (fail && url.endsWith('/camera')) throw Error('Offline'); return response(url);});
 const view = render(<PresentationPanel observation={observation} observationFresh/>); await act(async () => {}); fail = true;
 await act(async () => {await vi.advanceTimersByTimeAsync(1500);});
 expect(within(screen.getByRole('region', {name: 'Camera observation'})).getByText(/Stale/)).toBeVisible(); expect(screen.getByText('Position: 12, unknown')).toBeVisible();
 await act(async () => {view.rerender(<PresentationPanel observation={{...observation, connected: false}} observationFresh/>);});
 expect(screen.getAllByText(/Stale/)).toHaveLength(3); fail = false;
 await act(async () => {view.rerender(<PresentationPanel observation={observation} observationFresh/>);}); expect(screen.queryByText(/Stale/)).toBeNull();
});
it('hides 404 features and retries after a later poll', async () => {
 vi.useFakeTimers(); let missing = true; vi.stubGlobal('fetch', async (url: string) => missing ? {ok: false, status: 404, json: async () => ({code: 'not_found', detail: 'Unavailable'})} : response(url));
 await act(async () => {render(<PresentationPanel observation={observation} observationFresh/>);}); expect(screen.queryByRole('region', {name: 'Presentation observations'})).toBeNull();
 missing = false; await act(async () => {await vi.advanceTimersByTimeAsync(1500);}); expect(screen.getByText('Granite wall')).toBeVisible();
});
it('supersedes late world/session responses and aborts old requests', async () => {
 let release: ((value: ReturnType<typeof reply>) => void) | undefined; let oldSignal: AbortSignal | null | undefined;
 vi.stubGlobal('fetch', (url: string, options: RequestInit) => {if (url.endsWith('/selection') && !release) {oldSignal = options.signal; return new Promise<ReturnType<typeof reply>>(resolve => {release = resolve;});} return Promise.resolve(response(url));});
 const view = render(<PresentationPanel observation={observation} observationFresh/>); await act(async () => {});
 const next = {...observation, sessionId: 'replacement-session', identity: {colonyId: 'other', mapId: 1, loadToken: 'next'}};
 await act(async () => {view.rerender(<PresentationPanel observation={next} observationFresh/>);}); expect(oldSignal?.aborted).toBe(true);
 await act(async () => {release?.(reply(values.selection));}); expect(screen.queryByText('Granite wall')).toBeNull(); expect(screen.queryByText('Position: 12, unknown')).toBeNull(); expect(screen.getAllByText(/Observed world changed/)).toHaveLength(3);
});
it('renders the colonist dossier when the roster carries one', async () => {
 const dossier = {pawn: {id: 'a'}, colonist: true, job: {defName: 'Haul'}, needs: {mood: 0.42, breakRisk: 'minor'}, health: {summaryFraction: 0.8, hediffs: [{definition: {label: 'Cut'}, partLabel: 'Left arm', visible: true}]}, equipment: {equipped: [{thing: {label: 'Short bow'}}]}, biography: {childhood: {label: 'Urchin'}, skills: [{definition: {label: 'Shooting'}, level: 9, passion: 'Major'}], traits: [{defName: 'Tough'}]}, social: {memories: [{label: 'Ate without table', moodOffsetTotal: -3}]}};
 const roster = {roster: {context, listing: {...listing, totalCount: 1, returnedCount: 1}, colonists: [{pawnId: 'a', name: 'Ann', position: {x: 1, z: 2}, dossier}]}};
 vi.stubGlobal('fetch', async (url: string) => url.endsWith('/colonists') ? reply(roster) : response(url));
 await act(async () => {render(<PresentationPanel observation={observation} observationFresh/>);});
 const region = screen.getByRole('region', {name: 'Colonist observation'});
 expect(within(region).getByText('Ann')).toBeVisible(); expect(within(region).getByText(/Job: Haul/)).toBeVisible(); expect(within(region).getByText(/Mood 42% \(minor break risk\)/)).toBeVisible();
 expect(within(region).getByText(/Cut \(Left arm\)/)).toBeVisible(); expect(within(region).getByText(/Shooting 9\*\*/)).toBeVisible(); expect(within(region).getByText(/Traits: Tough/)).toBeVisible(); expect(within(region).getByText('Short bow')).toBeVisible(); expect(within(region).getByText(/Ate without table \(-3\)/)).toBeVisible();
});
it('joins the planner profile and work coverage onto the dossier by pawn id', async () => {
 const dossier = {pawn: {id: 'a'}, colonist: true, needs: {}, health: {}, equipment: {}, biography: {skills: [{definition: {label: 'Mining'}, level: 12}], traits: [{defName: 'Pyromaniac'}]}, social: {}};
 const roster = {roster: {context, listing: {...listing, totalCount: 2, returnedCount: 2}, colonists: [{pawnId: 'a', name: 'Ann', dossier}, {pawnId: 'b', name: 'Bob', dossier: {...dossier, pawn: {id: 'b'}}}]}};
 const profile = {pawn: 'a', age: 30, child: false, ranged: true, traits: [{name: 'Pyromaniac', degree: 0}, {name: 'FastLearner', degree: 0}], effects: {workSpeed: 0, learnRate: 0.75, moveSpeed: 0, sociable: 0, chemicalInterest: 0, flags: ['NoFirefighting', 'Pyromaniac']},
  skills: [{name: 'Mining', level: 12, stored: 11, passion: 'Major', disabled: false, learnFactor: 2.625}, {name: 'Medicine', level: 3, stored: 3, passion: '', disabled: false, learnFactor: 0.6125}, {name: 'Art', level: 0, stored: 0, passion: '', disabled: true, learnFactor: 0}], incapable: ['Hauling'], forbidden: ['Firefighter']};
 const work = {tick: 500, coverage: [{work: 'Doctor', demand: 1, owners: 0, capable: 0}, {work: 'Mining', demand: 1, owners: 1, capable: 1}], decaying: [{pawn: 'a', skill: 'Mining', level: 12}], pawns: [profile]};
 vi.stubGlobal('fetch', async (url: string) => url.endsWith('/colonists') ? reply(roster) : url.endsWith('/routines') ? reply(routines(work)) : response(url));
 await act(async () => {render(<PresentationPanel observation={observation} observationFresh/>);});
 const region = screen.getByRole('region', {name: 'Colonist observation'});
 expect(within(region).getByText(/Work roster · reviewed tick 500/)).toBeInTheDocument();
 const doctor = within(region).getByRole('row', {name: /Doctor/}); expect(doctor).toHaveClass('presentation-uncovered'); expect(within(doctor).getAllByRole('cell').map(c => c.textContent)).toEqual(['0', '1', '0']);
 expect(within(region).getByRole('row', {name: /Mining/})).not.toHaveClass('presentation-uncovered');
 expect(within(region).getByText('learning +0.75 · NoFirefighting · Pyromaniac')).toBeInTheDocument();
 expect(within(region).getByText('ranged weapon · forbidden: Firefighter · incapable: Hauling')).toBeInTheDocument();
 expect(within(region).getByText('Mining 12 (stored 11) ×2.63 decaying · Medicine 3 ×0.61')).toBeInTheDocument();
 expect(within(region).getAllByText('Trait effects')).toHaveLength(1);
});
