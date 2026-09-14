// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import {act,cleanup,render,screen} from '@testing-library/react';
import {afterEach,expect,it,vi} from 'vitest';
vi.mock('./ObservationDashboard',()=>({default:()=> <p>Observation surface</p>}));
import App from '../../App';
afterEach(()=>{cleanup();vi.useRealTimers();vi.unstubAllGlobals();history.replaceState(null,'','/');});
it('mounts the dashboard once the controller answers, regardless of backend field',async()=>{
 const fetcher=vi.fn(async()=>({ok:true,json:async()=>({service:'rimgovernor'})}));vi.stubGlobal('fetch',fetcher);
 await act(async()=>{render(<App/>);});expect(screen.getByText('Observation surface')).toBeVisible();
});
it('retries failed or malformed health and shows Help while waiting',async()=>{
 vi.useFakeTimers();const fetcher=vi.fn().mockResolvedValueOnce({ok:true,json:async()=>({unexpected:true})}).mockResolvedValue({ok:true,json:async()=>({service:'rimgovernor'})});vi.stubGlobal('fetch',fetcher);
 await act(async()=>{render(<App/>);});
 expect(screen.queryByText('Observation surface')).toBeNull();expect(screen.getByRole('status')).toHaveTextContent('Reconnecting');expect(screen.getAllByRole('link',{name:'Help'}).length).toBeGreaterThan(0);
 await act(async()=>{await vi.advanceTimersByTimeAsync(2000);});expect(screen.getByText('Observation surface')).toBeVisible();
});
