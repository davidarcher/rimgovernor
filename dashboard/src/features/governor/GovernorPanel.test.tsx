import '@testing-library/jest-dom/vitest';
import {act, cleanup, render, screen, waitFor, within} from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import {afterEach, expect, it, vi} from 'vitest';
import GovernorPanel from './GovernorPanel';
import {stepRows} from './fixtures';

const reply = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), {status, headers: {'content-type': 'application/json'}}));
const metrics = {tick: 29900, tps: 42.5, authority: {colony: 'c', map: 0, load: 'l', plan: 'plan-a', revision: '4', native: '9'}, last_step_ms: 3456, run: 'run-1abcdef', metrics: {native_calls: 40, native_errors: 1, reads_per_step_mean: 7.25, cache_hit_ratio: 0.5, native_queue_ms_mean: 2, native_exec_ms_mean: 30}};
const page = (events: unknown[], more = false) => ({events, next_since: 17, last_sequence: 17, more});
afterEach(() => {cleanup(); vi.unstubAllGlobals();});

it('renders the health strip, the feed newest first, and a picked step as a waterfall', async () => {
  const calls: string[] = [];
  vi.stubGlobal('fetch', vi.fn((url: string) => {
    calls.push(url);
    if (url === '/api/telemetry/metrics') return reply(metrics);
    if (url.startsWith('/api/telemetry/events')) return reply(page(url.includes('limit=1&') || url.endsWith('limit=1') ? [stepRows[0]] : stepRows));
    return reply({code: 'not_found', detail: 'Not found'}, 404);
  }));
  render(<GovernorPanel active/>);
  const health = screen.getByRole('region', {name: 'Governor health'});
  await waitFor(() => expect(within(health).getByText('29,900')).toBeInTheDocument());
  expect(within(health).getByText('· 42.5 TPS')).toBeInTheDocument();
  expect(within(health).getByText('3,456')).toBeInTheDocument();
  expect(within(health).getByText('Plan plan-a r4 · native 9')).toBeInTheDocument();
  expect(within(health).getByText('of 40 calls')).toBeInTheDocument();
  await waitFor(() => expect(screen.getByText('step done cause=live window_ticks=250')).toBeInTheDocument());
  // The first events read probes the newest sequence, then pages from a backfill behind it.
  expect(calls.filter(u => u.startsWith('/api/telemetry/events')).slice(0, 2)).toEqual(['/api/telemetry/events?limit=1', '/api/telemetry/events?limit=500']);
  const feed = screen.getByRole('region', {name: 'Governor events'});
  const rows = within(feed).getAllByRole('row').slice(1);
  expect(rows[0]).toHaveTextContent('scheduler_step');
  expect(rows[rows.length - 1]).toHaveTextContent('native_cache_hit');
  // native_decode is hidden until asked for.
  expect(within(feed).queryByText('native_decode', {selector: 'code'})).toBeNull();
  expect(within(feed).getByRole('checkbox', {name: /native_decode/})).not.toBeChecked();
  expect(screen.getByRole('region', {name: 'Step trace'})).toHaveTextContent('Pick a row');

  const user = userEvent.setup();
  await user.click(within(rows[0]).getByRole('button', {name: 'a1b2c3d4'}));
  const trace = screen.getByRole('region', {name: 'Step trace'});
  expect(trace).toHaveTextContent('scheduler_step: step done · 8 rows over 200.0 ms · tick 29,900 · sequence 10..17');
  const lines = within(trace).getAllByRole('row').slice(1);
  expect(lines).toHaveLength(5);
  expect(lines[1]).toHaveTextContent('native rimgovernor/observations_read_bundle');
  expect(lines[1]).toHaveTextContent('gate 10.0 · call 80.0 · decode 5.00 · native queue 20.0 exec 50.0');
  expect(lines[3]).toHaveTextContent('refused: stale_facts');
  expect(lines[3]).toHaveClass('governor-error');

  await user.type(within(feed).getByRole('searchbox'), 'routine-acquire');
  expect(within(feed).getAllByRole('row').slice(1)).toHaveLength(1);
  expect(within(feed).getByText('action=routine-acquire-1 receipt=refused reads=1')).toBeInTheDocument();
  await user.click(within(feed).getByRole('checkbox', {name: /worker_dispatch/}));
  expect(within(feed).getByText('No rows match the filter.')).toBeInTheDocument();
});

it('says so when serve runs without a flight recorder, and keeps readings through a failed refresh', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({code: 'not_found', detail: 'Telemetry is unavailable: the service runs without a flight recorder'}, 404)));
  const {unmount} = render(<GovernorPanel active/>);
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Telemetry is unavailable: Telemetry is unavailable: the service runs without a flight recorder'));
  expect(screen.queryByRole('region', {name: 'Governor events'})).toBeNull();
  unmount();
  vi.useFakeTimers();
  let fail = false;
  vi.stubGlobal('fetch', vi.fn((url: string) => {
    if (fail) return reply({code: 'unavailable', detail: 'Store closed'}, 503);
    return url === '/api/telemetry/metrics' ? reply(metrics) : reply(page(stepRows));
  }));
  render(<GovernorPanel active/>);
  await act(async () => {await vi.advanceTimersByTimeAsync(10);});
  const health = screen.getByRole('region', {name: 'Governor health'});
  expect(within(health).getByText('29,900')).toBeInTheDocument();
  fail = true;
  await act(async () => {await vi.advanceTimersByTimeAsync(2100);});
  expect(within(health).getByText('29,900')).toBeInTheDocument();
  expect(health).toHaveTextContent('Stale — last reading shown. Store closed');
  expect(screen.getByRole('region', {name: 'Governor events'})).toHaveTextContent('Stale — Store closed');
  expect(screen.getByText('step done cause=live window_ticks=250')).toBeInTheDocument();
  vi.useRealTimers();
});
