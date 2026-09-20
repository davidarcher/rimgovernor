import '@testing-library/jest-dom/vitest';
import {cleanup, render, screen, waitFor, within} from '@testing-library/react';
import {afterEach, expect, it, vi} from 'vitest';
import DevelopmentPanel from './DevelopmentPanel';
const reply = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), {status, headers: {'content-type': 'application/json'}}));
const development = {tick: 500, workers: 3, labor: [{work: 'Construction', free: 0}], capacity: 2, committed: [], rows: [
  {goal: 'ensure-research', score: 60, deficit: 0.6, risk: null, waitingSince: 100, selected: true, committed: false, reason: '', bottleneck: ''},
  {goal: 'ensure-comfort', score: 40, deficit: 0.5, risk: 0, waitingSince: 100, selected: false, committed: false, reason: 'labor_unavailable', bottleneck: 'Construction'},
  {goal: 'maintain-wood', score: 0, deficit: 0.3, risk: 1, waitingSince: 200, selected: false, committed: false, reason: 'risk_deferred', bottleneck: ''},
]};
afterEach(() => {cleanup(); vi.unstubAllGlobals();});
it('shows territory origins, current holds and resource reach separately', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({reviewsEnabled: true, methodsEnabled: true, resourceRunways: [], activeFamilies: [], lastReviewTick: 500, development: null, roster: null, sections: [],
    extent: {known: true, regions: 1, cells: 1}, resourceReach: {stage: 'base', reason: 'threat_present'},
    extentEligibility: {known: true, reason: '', regions: [{region: 0, stage: 'established', cells: 1, origins: ['facility'], facilities: ['bed'], activeFacilities: [], eligible: false, holdReasons: ['threat_present', 'facility_lost', 'route_unknown']}]},
  })));
  render(<DevelopmentPanel active/>);
  await waitFor(() => expect(screen.getByText(/Origins: facility/)).toBeInTheDocument());
  expect(screen.getByText(/Active facilities: none/)).toHaveTextContent('threat_present, facility_lost, route_unknown');
  expect(screen.getByText('Resource reach: base · threat_present')).toBeInTheDocument();
});
it('renders each goal progress record with its five fields', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({reviewsEnabled: true, methodsEnabled: true, resourceRunways: [], activeFamilies: [], lastReviewTick: 500, development: null, roster: null, sections: [], progress: [
    {goal: 'EnsureFoodSupply', method: 'acquire', expected: 'food runway toward target', lastProgress: 100, nextReview: 60100, blocked: 'prerequisite:EnsureCooking', cooldowns: []},
    {goal: 'MaintainWood', method: 'cut', expected: 'wood stock', lastProgress: 400, nextReview: 900, blocked: 'no_worker', cooldowns: [{key: 'cut/Plant_TreeOak', until: 1200}]},
  ]})));
  render(<DevelopmentPanel active/>);
  await waitFor(() => expect(screen.getByRole('region', {name: 'Goal progress'})).toBeInTheDocument());
  const food = within(screen.getByRole('row', {name: /EnsureFoodSupply/}));
  expect(food.getByText('acquire')).toBeInTheDocument();
  expect(food.getByText('food runway toward target')).toBeInTheDocument();
  expect(food.getByText('100')).toBeInTheDocument();
  expect(food.getByText('60,100')).toBeInTheDocument();
  expect(food.getByText('Needs EnsureCooking first')).toBeInTheDocument();
  expect(screen.getByText(/No capable worker available · cooldowns: cut\/Plant_TreeOak until 1,200/)).toBeInTheDocument();
});
it('renders the colony stage with its blocker and hold', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({reviewsEnabled: true, methodsEnabled: true, resourceRunways: [], activeFamilies: [], lastReviewTick: 500, development: null, roster: null, sections: [], progress: [],
    stage: {stage: 'Foothold', since: 10, blocker: 'shelter', reason: 'shelter unmet', held: true}})));
  render(<DevelopmentPanel active/>);
  await waitFor(() => expect(screen.getByTestId('colony-stage')).toHaveTextContent('Colony stage Foothold since tick 10 · next stage waits on shelter: shelter unmet · comfort-class development held'));
});
it('renders the recorded ranking with reasons, bottlenecks and labor', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({reviewsEnabled: true, methodsEnabled: true, resourceRunways: [], activeFamilies: [], lastReviewTick: 500, development, roster: null, sections: []})));
  render(<DevelopmentPanel active/>);
  await waitFor(() => expect(screen.getByText('Selected')).toBeInTheDocument());
  expect(screen.getByText('Waiting for labor (Construction)')).toBeInTheDocument();
  expect(screen.getByText('Deferred: outdoor risk')).toBeInTheDocument();
  expect(screen.getByText(/Free labor: Construction 0/)).toBeInTheDocument();
  expect(screen.queryByRole('status')).toBeNull();
});
it('hides itself when routine diagnostics are not enabled and marks failures stale', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({code: 'not_found', detail: 'Routine diagnostics are not enabled'}, 404)));
  const {container, unmount} = render(<DevelopmentPanel active/>);
  await waitFor(() => expect(container.querySelector('section')).toBeNull());
  unmount();
  vi.stubGlobal('fetch', vi.fn(() => reply({code: 'unavailable', detail: 'Store closed'}, 503)));
  render(<DevelopmentPanel active/>);
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Unavailable. Store closed'));
});

const runway = {resource: 'Steel', tick: 60000, windowDays: 15, thresholdDays: 5, reserve: 20, stock: 40, surfaceOre: 30, consumptionPerDay: 20, stockDays: 1, daysLeft: 2.5, deficit: true, target: 120};
it('shows recorded material forecasts even without a development ranking', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({reviewsEnabled: true, methodsEnabled: true, activeFamilies: [], lastReviewTick: 60000, development: null, roster: null, sections: [], resourceRunways: [runway, {...runway, resource: 'ComponentIndustrial', daysLeft: 0, stockDays: 0, stock: 0, surfaceOre: 0}]})));
  render(<DevelopmentPanel active/>);
  const steel = (await screen.findByRole('rowheader', {name: 'Steel'})).closest('tr')!;
  expect(within(steel).getAllByRole('cell').map(c => c.textContent)).toEqual(['2.5', '1', '40', '30', '20', '20', 'Deficit (threshold 5 days)', '120', 'Tick 60,000 · 15-day window']);
  const components = screen.getByRole('rowheader', {name: 'Components'}).closest('tr')!;
  expect(within(components).getAllByRole('cell')[0]).toHaveTextContent(/^0$/);
});
it('distinguishes unknown forecasts from a zero consumption rate without inventing infinity', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({reviewsEnabled: true, methodsEnabled: true, activeFamilies: [], lastReviewTick: 60000, development, roster: null, sections: [], resourceRunways: [
    {...runway, stock: null, surfaceOre: null, consumptionPerDay: null, stockDays: null, daysLeft: null, deficit: null},
    {...runway, resource: 'ComponentIndustrial', consumptionPerDay: 0, stockDays: null, daysLeft: null, deficit: false},
  ]})));
  render(<DevelopmentPanel active/>);
  const steel = (await screen.findByRole('rowheader', {name: 'Steel'})).closest('tr')!;
  expect(within(steel).getAllByRole('cell')[0]).toHaveTextContent(/^unknown$/);
  expect(within(steel).getAllByText('unknown')).toHaveLength(5);
  const components = screen.getByRole('rowheader', {name: 'Components'}).closest('tr')!;
  expect(within(components).getAllByText('No observed consumption')).toHaveLength(2);
  expect(within(components).getByText('Sufficient (threshold 5 days)')).toBeInTheDocument();
  expect(screen.queryByText(/Infinity|NaN/)).toBeNull();
});
it('shows an empty runway state', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({reviewsEnabled: true, methodsEnabled: true, activeFamilies: [], lastReviewTick: null, development: null, roster: null, sections: [], resourceRunways: []})));
  render(<DevelopmentPanel active/>);
  expect(await screen.findByText('No material runway has been recorded yet.')).toBeInTheDocument();
});
it('retains the last forecast and marks it stale when a refresh fails', async () => {
  vi.stubGlobal('fetch', vi.fn()
    .mockImplementationOnce(() => reply({reviewsEnabled: true, methodsEnabled: true, activeFamilies: [], lastReviewTick: 60000, development, roster: null, sections: [], resourceRunways: [runway]}))
    .mockImplementation(() => reply({code: 'unavailable', detail: 'Store closed'}, 503)));
  const {rerender} = render(<DevelopmentPanel active/>);
  await screen.findByRole('rowheader', {name: 'Steel'});
  rerender(<DevelopmentPanel active={false}/>);
  rerender(<DevelopmentPanel active/>);
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Stale — last recorded ranking and forecasts. Store closed'));
  expect(screen.getByRole('rowheader', {name: 'Steel'})).toBeInTheDocument();
  expect(screen.getByText('2.5')).toBeInTheDocument();
});
it('draws the colony grid overlay with its lines and origin', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({reviewsEnabled: true, methodsEnabled: true, resourceRunways: [], activeFamilies: [], lastReviewTick: 500, development: null, roster: null, sections: [], lootHolds: [],
    colonyGrid: {origin: {x: 40, z: 50}, pitch: 16, axes: [{x: 1, z: 0}, {x: 0, z: 1}], source: 'starter_shell', bounds: {width: 100, height: 80}}})));
  render(<DevelopmentPanel active/>);
  await waitFor(() => expect(screen.getByText(/Origin 40, 50 · Pitch 16 · From starter shell/)).toBeInTheDocument());
  const overlay = screen.getByRole('img', {name: /Colony grid overlay/});
  expect(overlay.querySelectorAll('[data-grid-line="x"]')).toHaveLength(6);
  expect(overlay.querySelectorAll('[data-grid-line="z"]')).toHaveLength(5);
  expect(overlay.querySelector('[data-grid-origin]')).not.toBeNull();
});
it('shows the pending layout re-site and its explanation', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({reviewsEnabled: true, methodsEnabled: true, resourceRunways: [], activeFamilies: [], lastReviewTick: 500, development: null, roster: null, sections: [], colonyGrid: null,
    layoutTidy: {active: true, reason: '', candidates: 1, proposal: {kind: 'field', item: 'Zone_7', from: {x: 20, z: 5, width: 2, height: 2}, to: {x: 17, z: 1, width: 11, height: 5}, crop: 'Plant_Rice', gain: 6, distance: 12, explanation: 'field Zone_7 sits off the grid'}}})));
  render(<DevelopmentPanel active/>);
  await waitFor(() => expect(screen.getByText(/Pending re-site: field Zone_7 from 2�2 at 20, 5 to 11�5 at 17, 1 � crop Plant_Rice � alignment gain 6/)).toBeInTheDocument());
  expect(screen.getByText('field Zone_7 sits off the grid')).toBeInTheDocument();
});
it('reports a missing colony grid', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({reviewsEnabled: true, methodsEnabled: true, resourceRunways: [], activeFamilies: [], lastReviewTick: 500, development: null, roster: null, sections: [], colonyGrid: null})));
  render(<DevelopmentPanel active/>);
  await waitFor(() => expect(screen.getByText('No colony grid has been established yet.')).toBeInTheDocument());
});
