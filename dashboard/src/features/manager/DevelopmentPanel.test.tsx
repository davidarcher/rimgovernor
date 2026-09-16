import '@testing-library/jest-dom/vitest';
import {cleanup, render, screen, waitFor} from '@testing-library/react';
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
  vi.stubGlobal('fetch', vi.fn(() => reply({reviewsEnabled: true, methodsEnabled: true, activeFamilies: [], lastReviewTick: 500, development})));
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
