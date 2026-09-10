// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import {render,screen,fireEvent,waitFor,cleanup} from '@testing-library/react';
import {afterEach,it,expect,vi} from 'vitest';
import GameControls from './GameControls';
import {tickRate} from './Throughput';

afterEach(()=>{cleanup();vi.unstubAllGlobals();vi.restoreAllMocks();});
it('sends session-bound native time and camera requests separately',async()=>{
 const fetch=vi.fn(async()=>({ok:true,json:async()=>({})}));vi.stubGlobal('fetch',fetch);
 render(<GameControls sessionId="load-a" connected stale={false} paused following={false} onError={()=>{}}/>);
 fireEvent.click(screen.getByRole('button',{name:'Play game at fast speed'}));
 await waitFor(()=>expect(screen.getByRole('button',{name:'Pause game'})).not.toBeDisabled());
 expect(fetch.mock.calls[0]).toEqual(['/api/time',expect.objectContaining({body:JSON.stringify({session_id:'load-a',speed:'Fast'}),headers:expect.objectContaining({'X-RimGovernor':'1'})})]);
 fireEvent.click(screen.getByRole('button',{name:/Follow actions/}));
 await waitFor(()=>expect(fetch).toHaveBeenCalledTimes(2));
 expect(fetch).toHaveBeenLastCalledWith('/api/camera/follow',expect.anything());
});
it('blocks stale controls and reports native refusals',async()=>{
 const onError=vi.fn();vi.stubGlobal('fetch',vi.fn(async()=>({ok:false,json:async()=>({detail:'Loaded colony changed'})})));
 const {rerender}=render(<GameControls sessionId="a" connected stale paused onError={onError}/>);
 expect(screen.getByRole('button',{name:'Pause game'})).toBeDisabled();
 rerender(<GameControls sessionId="a" connected stale={false} paused onError={onError}/>);
 fireEvent.click(screen.getByRole('button',{name:'Pause game'}));
 await waitFor(()=>expect(onError).toHaveBeenCalledWith('Error: Loaded colony changed'));
});
it('computes wall throughput including paused samples and rejects rewinds',()=>{
 expect(tickRate([{at:1,tick:0},{at:3,tick:600},{at:5,tick:600}])).toBe(150);
 expect(tickRate([{at:1,tick:600},{at:3,tick:0}])).toBeNull();
 expect(tickRate([{at:1,tick:0}])).toBeNull();
});

it('serializes camera requests and binds discrete actions to the displayed session',async()=>{
 let finish!: (value: unknown)=>void;
 const fetch=vi.fn(()=>new Promise(resolve=>{finish=resolve;}));vi.stubGlobal('fetch',fetch);
 render(<GameControls sessionId="load-a" connected stale={false} onError={()=>{}}/>);
 fireEvent.click(screen.getByRole('button',{name:'Pan camera left'}));
 fireEvent.click(screen.getByRole('button',{name:'Zoom camera in'}));
 expect(fetch).toHaveBeenCalledTimes(1);
 expect(fetch).toHaveBeenCalledWith('/api/camera/navigate',expect.objectContaining({body:JSON.stringify({session_id:'load-a',action:'left'})}));
 finish({ok:true,json:async()=>({})});
 await waitFor(()=>expect(screen.getByRole('button',{name:'Zoom camera in'})).not.toBeDisabled());
});
it('disables camera navigation in headless games',()=>{
 render(<GameControls sessionId="load-a" connected stale={false} headless onError={()=>{}}/>);
 expect(screen.getByRole('button',{name:'Pan camera left'})).toBeDisabled();
 expect(screen.getByRole('button',{name:'Zoom camera out'})).toBeDisabled();
});

it.each(['blur', 'hidden', 'unmount'])('releases player control without resuming on %s',async(reason)=>{
 const fetch=vi.fn(async(_url: string,_options?: RequestInit)=>({ok:true,json:async()=>({lease_id:'lease-a'})}));vi.stubGlobal('fetch',fetch);
 const {unmount}=render(<GameControls sessionId="load-a" connected stale={false} onError={()=>{}}/>);
 fireEvent.click(screen.getByRole('button',{name:'Take control'}));
 await screen.findByRole('button',{name:'You have control'});
 if(reason==='unmount') unmount();
 else if(reason==='hidden') {vi.spyOn(document,'hidden','get').mockReturnValue(true);fireEvent(document,new Event('visibilitychange'));}
 else fireEvent(window,new Event('blur'));
 await waitFor(()=>expect(fetch).toHaveBeenCalledWith('/api/input/release',expect.objectContaining({keepalive:true})));
 const call=fetch.mock.calls.find(c=>c[0]==='/api/input/release')!;
 expect(JSON.parse(call[1]!.body as string)).toMatchObject({session_id:'load-a',lease_id:'lease-a'});
 expect(JSON.parse(call[1]!.body as string).resume).toBeUndefined();
});

it('releases an acquisition that completes after the viewer loses focus',async()=>{
 let finish!: (value: unknown)=>void;
 const fetch=vi.fn((url: string)=>url==='/api/input/take'
   ? new Promise(resolve=>{finish=resolve;})
   : Promise.resolve({ok:true,json:async()=>({})}));vi.stubGlobal('fetch',fetch);
 render(<GameControls sessionId="load-a" connected stale={false} onError={()=>{}}/>);
 fireEvent.click(screen.getByRole('button',{name:'Take control'}));
 fireEvent(window,new Event('blur'));
 finish({ok:true,json:async()=>({lease_id:'late'})});
 await waitFor(()=>expect(fetch).toHaveBeenCalledWith('/api/input/release',expect.anything()));
 expect(screen.queryByRole('button',{name:'You have control'})).toBeNull();
});

it('requires ownership before selecting a colonist in the native game',async()=>{
 const fetch=vi.fn(async(_url: string,_options?: RequestInit)=>({ok:true,json:async()=>({lease_id:'lease-a'})}));vi.stubGlobal('fetch',fetch);
 render(<GameControls sessionId="load-a" connected stale={false} pawns={[{thing_id:'Human1',name:'Ada'}]} onError={()=>{}}/>);
 expect(screen.queryByRole('combobox',{name:'Select colonist in game'})).toBeNull();
 fireEvent.click(screen.getByRole('button',{name:'Take control'}));
 const select=await screen.findByRole('combobox',{name:'Select colonist in game'});
 await waitFor(()=>expect(select).not.toBeDisabled());
 fireEvent.change(select,{target:{value:'Human1'}});
 await waitFor(()=>expect(fetch).toHaveBeenCalledWith('/api/input/select',expect.objectContaining({body:expect.stringContaining('Human1')})));
 const call=fetch.mock.calls.find(c=>c[0]==='/api/input/select')!;
 expect(JSON.parse(call[1]!.body as string)).toMatchObject({session_id:'load-a',lease_id:'lease-a',pawn_id:'Human1'});
});
