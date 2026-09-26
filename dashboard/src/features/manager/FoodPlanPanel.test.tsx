import '@testing-library/jest-dom/vitest';
import {act, cleanup, render, screen, waitFor} from '@testing-library/react';
import {afterEach, expect, it, vi} from 'vitest';
import FoodPlanPanel from './FoodPlanPanel';
import {readFoodPlanStatus} from './foodPlanData';

const census = {foodPlanTick: 4200, foodPlan: {deliveredPerDay: 7, demandPerDay: 10, gapPerDay: 8, explain: 'Target includes buffer', portfolio: [
  {kind: 'Forage', id: 'berries', decision: 'Open', reason: 'close nutrition gap', deliveredPerDay: 4},
  {kind: 'Hunt', id: 'deer', decision: 'Open', reason: 'close nutrition gap', deliveredPerDay: 3},
  {kind: 'Crop', id: 'rice', decision: 'Hold', reason: 'lead exceeds runway', deliveredPerDay: 0},
  {kind: 'Reserve', id: 'stock-protection', decision: 'Hold', reason: 'capacity', deliveredPerDay: 0},
  {kind: 'Trade', id: 'trader', decision: 'Close', reason: 'surplus', deliveredPerDay: 0},
], unknown: [{kind: 'Fishing', id: 'river', decision: 'Hold', reason: 'unknown: nutrition_per_day', deliveredPerDay: 0}]}};
const reply = (value: unknown) => Promise.resolve(new Response(JSON.stringify(value)));
afterEach(() => {cleanup(); vi.unstubAllGlobals(); vi.useRealTimers();});
it('renders the server portfolio, zero Hold/Close rows and unknown reasons without deriving the gap', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply(census)));
  const {container} = render(<FoodPlanPanel active/>);
  await screen.findByText('Forage');
  expect(screen.getByText(/nutrition\/day delivered/)).toHaveTextContent('7 nutrition/day delivered · 10 demand · 8 plan gap');
  expect(screen.getByRole('img', {name: /Crop rice: Hold, 0 nutrition/})).toBeInTheDocument();
  expect(screen.getByRole('img', {name: /Trade trader: Close/})).toBeInTheDocument();
  expect(screen.getByText(/unknown: nutrition_per_day/)).toBeInTheDocument();
  expect(container.querySelector('.food-reserve')).toBeInTheDocument();
  expect(container.querySelector('.food-bar-Open')).toHaveStyle({width: `${4 / 11 * 100}%`});
});
it('retains last good data on refresh failure and clears it when the server invalidates the plan', async () => {
  vi.useFakeTimers({shouldAdvanceTime: true});
  const fetcher = vi.fn().mockImplementationOnce(() => reply(census)).mockRejectedValueOnce(Error('offline')).mockImplementation(() => reply({foodPlan: null, foodPlanTick: null}));
  vi.stubGlobal('fetch', fetcher);
  render(<FoodPlanPanel active/>);
  await screen.findByText('Forage');
  await act(() => vi.advanceTimersByTimeAsync(5000));
  expect(screen.getByRole('status')).toHaveTextContent('Stale — last recorded plan. offline');
  expect(screen.getByText('Forage')).toBeInTheDocument();
  await act(() => vi.advanceTimersByTimeAsync(5000));
  expect(screen.queryByText('Forage')).toBeNull();
  expect(screen.getByText('Food plan unavailable for the current tick.')).toBeInTheDocument();
});
it('does not poll while inactive and aborts on unmount', async () => {
  const fetcher = vi.fn(() => reply(census));
  vi.stubGlobal('fetch', fetcher);
  const view = render(<FoodPlanPanel active={false}/>);
  expect(fetcher).not.toHaveBeenCalled();
  view.rerender(<FoodPlanPanel active/>);
  await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(1));
  view.unmount();
});
it('accepts surplus gaps but rejects malformed rates, decisions and tick/plan pairs', () => {
  expect(readFoodPlanStatus({...census, foodPlan: {...census.foodPlan, gapPerDay: -4}}).plan?.gapPerDay).toBe(-4);
  for (const patch of [{demandPerDay: -1}, {deliveredPerDay: Infinity}, {portfolio: [{...census.foodPlan.portfolio[0], decision: 'Maybe'}]}, {portfolio: [...census.foodPlan.portfolio, census.foodPlan.portfolio[0]]}]) {
    expect(() => readFoodPlanStatus({...census, foodPlan: {...census.foodPlan, ...patch}})).toThrow();
  }
  expect(() => readFoodPlanStatus({...census, foodPlanTick: null})).toThrow();
  expect(() => readFoodPlanStatus({...census, foodPlan: null})).toThrow();
});
it('lists pet shortfalls the colony runway no longer carries (#708)', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({...census, foodPlan: {...census.foodPlan, petShortfalls: [{id: 'Thing_Cat5169', runwayDays: 0, nutritionPerDay: 0.3}]}})));
  render(<FoodPlanPanel active/>);
  expect(await screen.findByText('Pet shortfalls (1)')).toBeInTheDocument();
  expect(screen.getByText('Thing_Cat5169').closest('li')).toHaveTextContent('Thing_Cat5169 · 0 days of food · needs 0.3 nutrition/day');
});
it('reads a missing petShortfalls as none and rejects malformed rows', async () => {
  expect(readFoodPlanStatus(census).plan?.petShortfalls).toEqual([]);
  expect(() => readFoodPlanStatus({...census, foodPlan: {...census.foodPlan, petShortfalls: [{id: 'cat', runwayDays: -1, nutritionPerDay: 0.3}]}})).toThrow();
  vi.stubGlobal('fetch', vi.fn(() => reply(census)));
  render(<FoodPlanPanel active/>);
  expect(await screen.findByText('No pet below the minimum food runway.')).toBeInTheDocument();
});
