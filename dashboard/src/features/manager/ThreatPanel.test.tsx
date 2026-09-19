import '@testing-library/jest-dom/vitest';
import {cleanup, render, screen, waitFor} from '@testing-library/react';
import {afterEach, expect, it, vi} from 'vitest';
import ThreatPanel from './ThreatPanel';
import {readThreatStatus} from './threatData';
const reply = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), {status, headers: {'content-type': 'application/json'}}));
const census = {tick: 4200, rosterTick: 4200, colonists: 3, workers: 3, foodNutrition: 12, nutritionPerDay: 4.8, foodRunwayDays: 2.5, pendingFoodNutrition: null, foodCorpses: 0,
  raidPoints: 120.4, wealthTotal: 7400.2, wealthItems: 1200, wealthBuildings: null, wealthPawns: 5400, downed: 0, moodMean: 0.5, pawns: []};
afterEach(() => {cleanup(); vi.unstubAllGlobals();});
it('renders raid points and the wealth split, unknown figures as dashes', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply(census)));
  render(<ThreatPanel active/>);
  await waitFor(() => expect(screen.getByText('120 raid points')).toBeInTheDocument());
  expect(screen.getByText('7,400')).toBeInTheDocument();
  expect(screen.getByText('1,200')).toBeInTheDocument();
  expect(screen.getByText('—')).toBeInTheDocument();
  expect(screen.queryByRole('status')).toBeNull();
});
it('hides itself when the census is not served and marks failures stale', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({code: 'not_found', detail: 'Colony status is not enabled'}, 404)));
  const {container, unmount} = render(<ThreatPanel active/>);
  await waitFor(() => expect(container.querySelector('section')).toBeNull());
  unmount();
  vi.stubGlobal('fetch', vi.fn(() => reply({code: 'unavailable', detail: 'Colony status read failed: bridge closed'}, 503)));
  render(<ThreatPanel active/>);
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Unavailable. Colony status read failed: bridge closed'));
});
it('refuses a census whose figures are not finite non-negative numbers', () => {
  expect(readThreatStatus(census).raidPoints).toBe(120.4);
  expect(() => readThreatStatus({...census, raidPoints: -1})).toThrow('raidPoints');
  expect(() => readThreatStatus({...census, wealthTotal: 'lots'})).toThrow('wealthTotal');
  expect(() => readThreatStatus({...census, tick: -1})).toThrow('tick');
});
