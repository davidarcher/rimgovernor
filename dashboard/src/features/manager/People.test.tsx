// @vitest-environment jsdom
import {render,screen,fireEvent,cleanup,act} from '@testing-library/react';
import {afterEach,it,expect,vi} from 'vitest';
import People,{type Pawn,type NativePawn} from './People';
afterEach(()=>{cleanup();vi.useRealTimers();vi.unstubAllGlobals();});
const pawn:Pawn={thing_id:'Pawn1',name:'Sam',position:{x:2,z:3},job:null,drafted:false,downed:false,dead:false,mood:null,food:0,rest:0.5,armed:false,primary_weapon:null,needs_tend:null,bleeding:null};
const observation={pawns:[pawn],start_tick:10,end_tick:12,same_tick:false};
it('distinguishes missing needs from zero and never labels a jobless pawn idle',()=>{
 render(<People observation={observation} stale={true}/>);
 fireEvent.click(screen.getByRole('button',{name:'Sam'}));
 expect(screen.getAllByText('Unknown').length).toBe(3);
 expect(screen.getByText('0%')).toBeTruthy();expect(screen.getByText('50%')).toBeTruthy();
 expect(screen.getAllByText('No current job').length).toBeGreaterThan(0);
 expect(screen.getByRole('status').textContent).toContain('last observations');
 expect(screen.getByText(/advanced during collection/)).toBeTruthy();
});
const native:NativePawn={thingId:'Pawn1',name:'Sam',position:{x:2,z:3},job:'Hauling',drafted:false,downed:false,dead:false,
 needs:{mood:0.6,food:0,rest:0.5},equipment:{armed:true,primaryLabel:'Revolver',apparel:[{label:'Duster',conditionPct:0.8}],equipped:[],inventoryWeapons:[]},
 bio:{ageBiological:27,childhood:'Farm child',traits:[{label:'Kind',description:'Cares about others',suppressed:false}],skills:[{label:'Plants',level:8,passion:'Major'}]},
 thoughts:{hasThoughtHandler:true,memories:[{label:'Ate without table',moodOffset:-3,count:1}],situational:[],situationalCacheStale:true}};
it('refreshes a selected dossier, retains it on failure and stops hidden polling',async()=>{
 vi.useFakeTimers();
 const response=(p:NativePawn,tick:number)=>({ok:true,json:async()=>({sessionId:'load-a',pawns:[p],startTick:tick,endTick:tick,observedAt:1})});
 const fetch=vi.fn().mockResolvedValueOnce(response(native,10)).mockResolvedValueOnce(response({...native,job:'Sowing'},20)).mockResolvedValue({ok:false});
 vi.stubGlobal('fetch',fetch);
 const {rerender}=render(<People sessionId="load-a" active={true} headless={true} stale={false}/>);
 await act(async()=>{});
 fireEvent.click(screen.getByRole('button',{name:'Sam'}));
 expect(screen.getByText('Duster · 80% condition')).toBeTruthy();
 expect(screen.getByText('Ate without table')).toBeTruthy();
 expect(screen.getByText(/refresh its cache/)).toBeTruthy();
 await act(async()=>{await vi.advanceTimersByTimeAsync(2500);});
 expect(screen.getAllByText('Sowing').length).toBeGreaterThan(0);
 expect(screen.getByRole('list', {name:'Recent observed actions'}).textContent).toContain('Hauling');
 await act(async()=>{await vi.advanceTimersByTimeAsync(2500);});
 expect(screen.getByRole('status').textContent).toContain('Last readings retained');
 expect(screen.getByText('Ate without table')).toBeTruthy();
 rerender(<People sessionId="load-a" active={false} headless={true} stale={false}/>);
 await act(async()=>{await vi.advanceTimersByTimeAsync(10000);});
 expect(fetch).toHaveBeenCalledTimes(3);
 rerender(<People sessionId="load-b" active={false} headless={true} stale={false}/>);
 expect(screen.queryByText('Ate without table')).toBeNull();
});
it('does not retain stale pawn details after removal',()=>{
 const {rerender}=render(<People observation={observation} stale={false}/>);
 fireEvent.click(screen.getByRole('button',{name:'Sam'}));
 rerender(<People observation={{...observation,pawns:[]}} stale={false}/>);
 expect(screen.queryByText('50%')).toBeNull();
 expect(screen.getByText(/selected colonist is no longer/)).toBeTruthy();
});
