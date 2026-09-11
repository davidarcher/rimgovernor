// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import {act, cleanup, render, screen, within} from '@testing-library/react';
import {afterEach, expect, it, vi} from 'vitest';
import NotificationPanel from './NotificationPanel';
import type {ObservationState} from './observationData';
const observation: ObservationState = {sessionId: 'session', connected: true, mode: 'manual', status: {label: ''}, identity: {colonyId: 'colony', mapId: 0, loadToken: 'load'}, game: {tick: 1, paused: true, observedAt: null, stale: false}, activePlanId: null};
const value = {notifications: {context: {identity: observation.identity, tick: '9007199254740993'}, letters: {observed: {letters: [{id: 'letter', label: 'Visitors', text: 'At the colony', choices: [{index: 1, text: 'Accept'}]}], listing: {returnedCount: 1, totalCount: 2, truncated: true}}}, messages: {observed: {listing: {complete: true, totalCount: 0, returnedCount: 0}}}, alerts: {unavailable: {reason: 'UNAVAILABLE_REASON_READ_FAILED', detail: 'Alerts could not be observed'}}}};
const reply = (data: unknown = value) => ({ok: true, text: async () => JSON.stringify(data)});
afterEach(() => {cleanup(); vi.useRealTimers(); vi.unstubAllGlobals();});
it('renders successful sections alongside unavailable alerts with no action controls', async () => {
 const fetcher = vi.fn(async (url: string, options: RequestInit) => {expect(url).toBe('/api/presentation/notifications'); expect(options.method).toBe('GET'); expect(options.body).toBeUndefined(); return reply();}); vi.stubGlobal('fetch', fetcher);
 await act(async () => {render(<NotificationPanel observation={observation} observationFresh/>);});
 expect(screen.getByText('Visitors')).toBeVisible(); expect(screen.getByText('At the colony')).toBeVisible(); expect(screen.getByText(/Truncated/)).toBeVisible(); expect(screen.getByText('No messages.')).toBeVisible(); expect(screen.getByText(/Alerts could not/)).toBeVisible(); expect(screen.queryByText('Accept')).toBeNull(); expect(screen.queryByRole('button')).toBeNull(); expect(fetcher).toHaveBeenCalledTimes(1);
});
it('retains last good notifications on errors, disconnects and reconnects', async () => {
 vi.useFakeTimers(); let fail = false; vi.stubGlobal('fetch', async () => {if (fail) throw Error('Offline'); return reply();});
 const view = render(<NotificationPanel observation={observation} observationFresh/>); await act(async () => {}); fail = true;
 await act(async () => {await vi.advanceTimersByTimeAsync(1500);}); expect(screen.getByText(/Stale/)).toBeVisible(); expect(screen.getByText('Visitors')).toBeVisible();
 await act(async () => {view.rerender(<NotificationPanel observation={{...observation, connected: false}} observationFresh/>);});
 fail = false; await act(async () => {view.rerender(<NotificationPanel observation={observation} observationFresh/>);}); expect(screen.queryByText(/Stale/)).toBeNull();
});
it('hides unavailable404 feature and supersedes late session/world results', async () => {
 let release: ((value: ReturnType<typeof reply>) => void) | undefined; let signal: AbortSignal | null | undefined;
 vi.stubGlobal('fetch', (_: string, options: RequestInit) => {if (!release) {signal = options.signal; return new Promise<ReturnType<typeof reply>>(resolve => {release = resolve;});} return Promise.resolve({ok: false, status: 404, json: async () => ({code: 'not_found', detail: 'Unavailable'})});});
 const view = render(<NotificationPanel observation={observation} observationFresh/>); await act(async () => {});
 await act(async () => {view.rerender(<NotificationPanel observation={{...observation, sessionId: 'next', identity: {colonyId: 'colony', mapId: 0, loadToken: 'new'}}} observationFresh/>);}); expect(signal?.aborted).toBe(true);
 await act(async () => {release?.(reply());}); expect(screen.queryByRole('region', {name: 'Notifications'})).toBeNull(); expect(screen.queryByText('Visitors')).toBeNull();
});
it('shows transient text and alert explanations without defaulting unknown flags', async () => {
 const data = {notifications: {...value.notifications, messages: {observed: {messages: [{text: 'A visitor arrived', ageSeconds: 0, expired: false}]}}, alerts: {observed: {alerts: [{label: 'Low medicine', explanation: 'Stock is low', readIssue: {reason: 'UNAVAILABLE_REASON_NOT_OBSERVED'}}]}}}};
 vi.stubGlobal('fetch', async () => reply(data)); await act(async () => {render(<NotificationPanel observation={observation} observationFresh/>);});
 expect(screen.getByText('A visitor arrived')).toBeVisible(); expect(screen.getByText('Expired: no · Age: 0 seconds')).toBeVisible(); expect(screen.getByText('Stock is low')).toBeVisible(); expect(screen.getByText('Active: unknown')).toBeVisible(); expect(screen.getByText(/Partial read: UNAVAILABLE_REASON_NOT_OBSERVED/)).toBeVisible();
});
it('rejects mismatched worlds and malformed successful refreshes while retaining data stale', async () => {
 vi.useFakeTimers(); let data: unknown = value; vi.stubGlobal('fetch', async () => reply(data));
 await act(async () => {render(<NotificationPanel observation={observation} observationFresh/>);});
 data = {notifications: {...value.notifications, context: {...value.notifications.context, identity: {colonyId: 'different', mapId: 1, loadToken: 'other'}}}};
 await act(async () => {await vi.advanceTimersByTimeAsync(1500);}); expect(screen.getByText(/Observed world changed/)).toBeVisible(); expect(screen.getByText('Visitors')).toBeVisible();
 data = {notifications: {}}; await act(async () => {await vi.advanceTimersByTimeAsync(1500);}); expect(within(screen.getByRole('region', {name: 'Notifications'})).getByText(/Stale/)).toBeVisible();
});
