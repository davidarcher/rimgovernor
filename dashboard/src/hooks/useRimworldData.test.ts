import {act,renderHook,waitFor,cleanup} from '@testing-library/react';
import {afterEach,describe,expect,it,vi} from 'vitest';
import {useRimWorldData} from './useRimworldData';
import {fetchRimWorldData} from '../services/rimworldApi';
vi.mock('../services/rimworldApi',()=>({fetchRimWorldData:vi.fn(),setApiBaseUrl:vi.fn()}));
afterEach(cleanup);
describe('dashboard refresh',()=>{
 it('keeps existing data and does not enter full-page loading during refresh',async()=>{
   const state={gameState:{program_state:'Playing'},colonists:[],colonistsDetailed:[]} as any;
   vi.mocked(fetchRimWorldData).mockResolvedValueOnce(state);
   const changed=vi.fn();
   const {result}=renderHook(()=>useRimWorldData('/rimapi/api/v1',changed,false,0));
   await waitFor(()=>expect(result.current.data).toBe(state));
   let finish:(value:any)=>void=()=>{};
   vi.mocked(fetchRimWorldData).mockImplementationOnce(()=>new Promise(resolve=>{finish=resolve;}));
   let refreshing:Promise<void>;
   act(()=>{refreshing=result.current.refresh();});
   expect(result.current.loading).toBe(false);expect(result.current.data).toBe(state);
   await act(async()=>{finish(state);await refreshing;});
   expect(changed).not.toHaveBeenCalled(); // Zero pawns is data, not a remount trigger.
 });
 it('keeps the last good data and explains a failed background request',async()=>{
   const state={gameState:{program_state:'Playing'},colonists:[]} as any;
   vi.mocked(fetchRimWorldData).mockResolvedValueOnce(state);
   const changed=vi.fn();
   const {result}=renderHook(()=>useRimWorldData('/rimapi/api/v1',changed,false,0));
   await waitFor(()=>expect(result.current.data).toBe(state));
   vi.mocked(fetchRimWorldData).mockRejectedValueOnce(new Error('RIMAPI 503: map unavailable'));
   await act(async()=>{await result.current.refresh();});
   expect(result.current.data).toBe(state);expect(result.current.loading).toBe(false);
   expect(result.current.error).toContain('map unavailable');
 });
});
