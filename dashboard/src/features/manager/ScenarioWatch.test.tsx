// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import {act, cleanup, render, screen} from '@testing-library/react';
import {afterEach, expect, it, vi} from 'vitest';
import ScenarioWatch from './ScenarioWatch';

afterEach(() => {cleanup(); vi.useRealTimers(); vi.unstubAllGlobals();});
it('only reads retained state, retains failures, and follows runtime replacement', async () => {
  vi.useFakeTimers();
  const state = {sessionId:'a', status:{label:'Building shelter'}, game:{tick:42,paused:false}, mode:'automate',headless:true,feed:[]};
  const request = vi.fn().mockResolvedValueOnce({ok:true,json:async()=>state})
    .mockRejectedValueOnce(Error('Disconnected'))
    .mockResolvedValue({ok:true,json:async()=>({...state,sessionId:'b',status:{label:'Resumed colony'}})});
  vi.stubGlobal('fetch', request);
  await act(async()=>{render(<ScenarioWatch/>);});
  expect(screen.getByText('Building shelter')).toBeVisible();
  expect(screen.queryByRole('button')).toBeNull();
  await act(async()=>{await vi.advanceTimersByTimeAsync(2000);});
  expect(screen.getByText('Building shelter')).toBeVisible();
  expect(screen.getByRole('status')).toHaveTextContent('Disconnected');
  await act(async()=>{await vi.advanceTimersByTimeAsync(2000);});
  expect(screen.getByText('Resumed colony')).toBeVisible();
  expect(screen.queryByText('Building shelter')).toBeNull();
  for (const [url, options] of request.mock.calls) {
    expect(url).toBe('/api/state'); expect(options.method).toBeUndefined();
  }
});
