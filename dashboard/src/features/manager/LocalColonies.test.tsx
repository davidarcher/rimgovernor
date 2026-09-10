// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen, act } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import LocalColonies from './LocalColonies';

afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });
const colony = { id: 'abc', name: 'Winter survival', url: 'http://127.0.0.1:45123/', display: 'xvfb' };
it('opens published colonies in separate tabs and explains unavailable dashboards', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ colonies: [colony, { ...colony, id: 'def', name: 'Headless test', url: null, display: 'headless' }] }) })));
  render(<LocalColonies/>);
  const link = await screen.findByRole('link', { name: 'Open colony ↗' });
  expect(link).toHaveAttribute('href', colony.url);
  expect(link).toHaveAttribute('target', 'colony-abc');
  expect(screen.getByText('Dashboard port not published')).toBeVisible();
  expect(screen.getByText('Headless · no game image')).toBeVisible();
});
it('retains the last directory on failure and removes stopped workers on recovery', async () => {
  vi.useFakeTimers();
  const fetcher = vi.fn().mockResolvedValueOnce({ok: true, json: async () => ({colonies: [colony]})})
    .mockRejectedValueOnce(Error('Docker unavailable'))
    .mockResolvedValue({ok: true, json: async () => ({colonies: []})});
  vi.stubGlobal('fetch', fetcher);
  await act(async () => { render(<LocalColonies/>); });
  expect(screen.getByText(colony.name)).toBeVisible();
  await act(async () => { await vi.advanceTimersByTimeAsync(10000); });
  expect(screen.getByText(colony.name)).toBeVisible();
  expect(screen.getByRole('status')).toHaveTextContent('Showing the last discovered colonies');
  await act(async () => { await vi.advanceTimersByTimeAsync(10000); });
  expect(screen.queryByText(colony.name)).toBeNull();
  expect(screen.getByText('No running RimGovernor Docker colonies found.')).toBeVisible();
});
