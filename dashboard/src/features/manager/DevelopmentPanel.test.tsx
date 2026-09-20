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