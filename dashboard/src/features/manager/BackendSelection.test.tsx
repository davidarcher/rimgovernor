// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import {act,cleanup,render,screen} from '@testing-library/react';
import {afterEach,expect,it,vi} from 'vitest';
vi.mock('./BridgeColony',()=>({default:()=> <textarea aria-label="Player draft" defaultValue="Saved draft"/>}));
vi.mock('./LocalColonies',()=>({default:()=> <p>Colony directory</p>}));
vi.mock('./ScenarioWatch',()=>({default:()=> <p>Scenario view</p>}));
vi.mock('./ObservationDashboard',()=>({default:()=> <p>Observation surface</p>}));
import App from '../../App';
afterEach(()=>{cleanup();vi.useRealTimers();vi.unstubAllGlobals();history.replaceState(null,'','/');});
it('selects the observation view on every path for the read service',async()=>{
 history.replaceState(null,'','/colonies');vi.stubGlobal('fetch',vi.fn(async()=>({ok:true,json:async()=>({service:'rimgovernor',backend:'go'})})));
 await act(async()=>{render(<App/>);});expect(screen.getByText('Observation surface')).toBeVisible();expect(screen.queryByText('Colony directory')).toBeNull();
});
it('keeps the existing backend and draft mounted after initial detection',async()=>{
 vi.useFakeTimers();const fetcher=vi.fn(async()=>({ok:true,json:async()=>({service:'rimgovernor'})}));vi.stubGlobal('fetch',fetcher);
 await act(async()=>{render(<App/>);});const draft=screen.getByRole('textbox');
 await act(async()=>{await vi.advanceTimersByTimeAsync(10000);});expect(screen.getByRole('textbox')).toBe(draft);expect(draft).toHaveValue('Saved draft');expect(fetcher).toHaveBeenCalledTimes(1);
});
it('retries failed or malformed health without mounting a guessed backend',async()=>{
 vi.useFakeTimers();const fetcher=vi.fn().mockResolvedValueOnce({ok:true,json:async()=>({unexpected:true})}).mockResolvedValue({ok:true,json:async()=>({service:'rimgovernor',backend:'go'})});vi.stubGlobal('fetch',fetcher);
 await act(async()=>{render(<App/>);});expect(screen.queryByRole('textbox')).toBeNull();expect(screen.getByRole('status')).toHaveTextContent('Reconnecting');
 await act(async()=>{await vi.advanceTimersByTimeAsync(2000);});expect(screen.getByText('Observation surface')).toBeVisible();
});

