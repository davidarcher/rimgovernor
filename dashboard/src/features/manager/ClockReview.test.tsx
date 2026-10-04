import {act, fireEvent, render, screen, cleanup} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import {afterEach, expect, it, vi} from 'vitest';
import ClockReview from './ClockReview';
import {decodeClockReview} from './playerData';

afterEach(() => {cleanup(); vi.unstubAllGlobals();});
const review = {revision: '2', inboxCursor: '3', reviewedCursor: '3', acknowledgedCursor: '0', holds: [{kind: 'gap', fromCursor: '1', throughCursor: '3'}]};
it('acknowledges only an explicit click using the displayed revision without enabling orders', async () => {
  const fetcher = vi.fn(async (_url: string, options?: RequestInit) => new Response(JSON.stringify(options?.method === 'POST' ? {...review, revision: '3', acknowledgedCursor: '3', holds: []} : review), {status: 200}));
  vi.stubGlobal('fetch', fetcher);
  await act(async () => {render(<ClockReview token="secret"/>);});
  expect(screen.getByText(/Some event history is missing/)).toBeTruthy();
  expect(fetcher).toHaveBeenCalledTimes(1);
  await act(async () => {fireEvent.click(screen.getByRole('button', {name: 'Acknowledge inspected interruptions'}));});
  expect(fetcher).toHaveBeenCalledTimes(2);
  const [url, options] = fetcher.mock.calls[1];
  expect(url).toBe('/api/player/clock/acknowledge');
  expect(options?.headers).toEqual({'Content-Type': 'application/json', 'X-RimGovernor-Player': 'secret'});
  expect(JSON.parse(options?.body as string) as Record<string, unknown>).toMatchObject({expectedRevision: '2', throughCursor: '3'});
  expect(screen.getByText(/No captured clock interruptions/)).toBeTruthy();
});
it('enables acknowledge whenever reviewed events are unacknowledged, even with no holds', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({...review, holds: []}), {status: 200})));
  await act(async () => {render(<ClockReview token="secret"/>);});
  expect(screen.getByRole('button', {name: 'Acknowledge inspected interruptions'})).toBeEnabled();
});
it('disables acknowledge when nothing is open', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({...review, acknowledgedCursor: '3', holds: []}), {status: 200})));
  await act(async () => {render(<ClockReview token="secret"/>);});
  expect(screen.getByRole('button', {name: 'Acknowledge inspected interruptions'})).toBeDisabled();
});
it('rejects malformed or contradictory cursor evidence', () => {
  expect(() => decodeClockReview({...review, reviewedCursor: '4'})).toThrow();
  expect(() => decodeClockReview({...review, revision: '02'})).toThrow();
});
