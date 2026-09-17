// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import {act, cleanup, fireEvent, render, screen} from '@testing-library/react';
import {afterEach, expect, it, vi} from 'vitest';
import PlayerControls from './PlayerControls';
import type {ObservationState} from './observationData';
const world={colonyId:'colony',mapId:0,loadToken:'load'};
const observation: ObservationState={sessionId:'http-session',connected:true,mode:'manual',status:{label:'Observed'},identity:world,game:{tick:10,paused:true,observedAt:'2026-09-10T00:00:00Z',stale:false},activePlanId:null};
const building={defName:'Wall',stuff:'WoodLog',x:1,z:2,rotation:'north'};
const state={enabled:false,observationKnown:false,generation:null};
const current={record:null,state,error:null};
const response=(value:unknown,status=200)=>new Response(JSON.stringify(value),{status});
function fill(){for(const [label,value] of [['Definition name','Wall'],['Material (optional)','WoodLog'],['Map X','1'],['Map Z','2']])fireEvent.change(screen.getByLabelText(label),{target:{value}});}
function setup(handler:(url:string,options:RequestInit)=>Promise<Response>, readControl:()=>unknown=()=>current){
 let counter=0;vi.stubGlobal('crypto',{randomUUID:()=>`request-${++counter}`});
 const fetcher=vi.fn((url:string,options:RequestInit={})=> url==='/api/player/session'?Promise.resolve(response({token:'secret',mode:'explicit-player'})):url==='/api/player/control'?Promise.resolve(response(readControl())):handler(url,options));
 vi.stubGlobal('fetch',fetcher);return fetcher;
}
afterEach(()=>{cleanup();vi.useRealTimers();vi.unstubAllGlobals();});
it('hides controls on read-only bootstrap',async()=>{vi.stubGlobal('fetch',vi.fn(async()=>response({code:'not_found',detail:'Missing'},404)));await act(async()=>{render(<PlayerControls observation={observation} observationFresh/>);});expect(screen.queryByRole('button')).toBeNull();});
it('submits an immutable explicit plan without resuming and preserves editable draft',async()=>{
 const fetcher=setup(async(url)=>{if(url==='/api/buildings/plans')return response({requestId:'request-1',expected:world,building,planId:'plan',actionId:'action',revision:'9007199254740993'},201);throw Error(url);});
 const view=render(<PlayerControls observation={observation} observationFresh/>);await act(async()=>{});fill();
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 expect(screen.getByText('Plan plan · Revision 9007199254740993')).toBeVisible();expect(screen.getByLabelText('Definition name')).toHaveValue('Wall');
 expect(fetcher).toHaveBeenCalledWith('/api/buildings/plans',expect.objectContaining({headers:{'Content-Type':'application/json','X-RimGovernor-Player':'secret'},body:JSON.stringify({requestId:'request-1',expected:world,building})}));
 expect(fetcher.mock.calls.filter(([url])=>url.includes('/resume'))).toHaveLength(0);
 view.rerender(<PlayerControls observation={{...observation,connected:false,game:{...observation.game,stale:true}}} observationFresh={false}/>);
 expect(screen.getByLabelText('Definition name')).toHaveValue('Wall');expect(screen.getByRole('button',{name:'Resume'})).toBeDisabled();expect(screen.getByRole('button',{name:'Pause'})).toBeEnabled();
});
it('recovers a lost submission through GET without another POST or request ID',async()=>{
 const fetcher=setup(async(url)=>{if(url==='/api/buildings/plans')throw Error('Lost response');if(url==='/api/buildings/submission?requestId=request-1')return response({requestId:'request-1',expected:world,building,planId:'plan',actionId:'action',revision:'1'});throw Error(url);});
 await act(async()=>{render(<PlayerControls observation={observation} observationFresh/>);});fill();
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 expect(screen.getByRole('button',{name:'Submit building plan'})).toBeDisabled();
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Check submission result'}));});
 expect(screen.getByText('Plan plan · Revision 1')).toBeVisible();expect(fetcher.mock.calls.filter(([,options])=>options?.method==='POST')).toHaveLength(1);
});
it('keeps Pause independent of pending Resume and ignores its late permission state',async()=>{
 let finish:((response:Response)=>void)|undefined;
 const fetcher=setup(async(url)=>{
  if(url==='/api/buildings/plans')return response({requestId:'request-1',expected:world,building,planId:'plan',actionId:'action',revision:'1'});
  if(url==='/api/player/control/resume')return new Promise<Response>(resolve=>{finish=resolve;});
  if(url==='/api/player/control/pause')return response({record:{requestId:'request-3',kind:'pause',expected:world,phase:'paused',nativeGeneration:'0'},state,error:null});
  throw Error(url);
 });
 await act(async()=>{render(<PlayerControls observation={observation} observationFresh/>);});fill();await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Resume'}));});
 expect(screen.getByRole('button',{name:'Pause'})).toBeEnabled();
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Pause'}));});
 expect(fetcher.mock.calls.filter(([url])=>url==='/api/player/control/pause')).toHaveLength(1);
 await act(async()=>{finish?.(response({record:{requestId:'request-2',kind:'resume',expected:world,phase:'running',nativeGeneration:'2'},state:{enabled:true,observationKnown:true,generation:{colony:'colony',load:'load',map:0,plan:'root/colony/load/0',revision:'1',native:'2'}},error:null}));});
 expect(screen.queryByText('Bot running: orders enabled')).toBeNull();expect(screen.getByText(/Historical result: running/)).toBeVisible();
});
it('refetches memory token after server session change without reposting pending intent',async()=>{
 const fetcher=setup(async()=>{throw Error('Disconnected');});
 const view=render(<PlayerControls observation={observation} observationFresh/>);await act(async()=>{});fill();await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 await act(async()=>{view.rerender(<PlayerControls observation={{...observation,sessionId:'new-server',identity:{...world,loadToken:'new-load'}}} observationFresh/>);});
 expect(fetcher.mock.calls.filter(([url])=>url==='/api/player/session')).toHaveLength(2);expect(fetcher.mock.calls.filter(([,options])=>options?.method==='POST')).toHaveLength(1);expect(screen.getByLabelText('Definition name')).toHaveValue('Wall');expect(screen.getByText('request-1')).toBeVisible();
});
it('retains503 uncertainty through read recovery',async()=>{
 const historical={record:{requestId:'previous',kind:'pause',expected:world,phase:'paused',nativeGeneration:'0'},state,error:null};
 const uncertain={record:{requestId:'request-2',kind:'resume',expected:world,phase:'uncertain',nativeGeneration:'0'},state,error:{code:'uncertain',detail:'Inspect this request'}};
 const fetcher=setup(async(url)=>{
  if(url==='/api/buildings/plans')return response({requestId:'request-1',expected:world,building,planId:'plan',actionId:'action',revision:'9007199254740993'});
  if(url==='/api/player/control/resume')return response(uncertain,503);
  if(url==='/api/player/control?requestId=request-2')return response({...uncertain,error:null});
  throw Error(url);
 },()=>historical);
 await act(async()=>{render(<PlayerControls observation={observation} observationFresh/>);});fill();await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Resume'}));});
 expect(fetcher).toHaveBeenCalledWith('/api/player/control/resume',expect.objectContaining({body:JSON.stringify({requestId:'request-2',expected:world})}));
 expect(screen.getByText(/Historical result: uncertain/)).toBeVisible();expect(screen.getByRole('button',{name:'Resume'})).toBeDisabled();
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Check resume result'}));});
 expect(fetcher.mock.calls.filter(([url])=>url==='/api/player/control/resume')).toHaveLength(1);expect(screen.queryByText('Bot running: orders enabled')).toBeNull();
});
it('rebootstraps after403 without automatically reposting the frozen request',async()=>{
 const fetcher=setup(async()=>response({code:'player_auth',detail:'Token expired'},403));
 await act(async()=>{render(<PlayerControls observation={observation} observationFresh/>);});fill();await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 expect(fetcher.mock.calls.filter(([url])=>url==='/api/player/session')).toHaveLength(2);expect(fetcher.mock.calls.filter(([,options])=>options?.method==='POST')).toHaveLength(1);
 expect(screen.getByText('request-1')).toBeVisible();expect(screen.getByLabelText('Definition name')).toHaveValue('Wall');
});
it('preserves draft and submission on malformed control refresh, and never stops on unmount',async()=>{
 vi.useFakeTimers();let malformed=false;
 const fetcher=setup(async()=>response({requestId:'request-1',expected:world,building,planId:'plan',actionId:'action',revision:'1'}),()=>malformed?{...current,state:{enabled:true,observationKnown:false,generation:null}}:current);
 const view=render(<PlayerControls observation={observation} observationFresh/>);await act(async()=>{});fill();await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 malformed=true;await act(async()=>{await vi.advanceTimersByTimeAsync(1500);});
 expect(screen.getByText('Plan plan · Revision 1')).toBeVisible();expect(screen.getByLabelText('Definition name')).toHaveValue('Wall');expect(screen.getByRole('button',{name:'Resume'})).toBeDisabled();
 view.unmount();expect(fetcher.mock.calls.filter(([url])=>url==='/api/player/control/pause')).toHaveLength(0);
});
it('allows a new explicit Resume after fresh later Pause, retaining the frozen uncertain request',async()=>{
 vi.useFakeTimers();let latest:unknown=current;
 const fetcher=setup(async(url,options)=>{
  if(url==='/api/buildings/plans')return response({requestId:'request-1',expected:world,building,planId:'plan',actionId:'action',revision:'1'});
  if(url==='/api/player/control/resume'){
   if(options.body===JSON.stringify({requestId:'request-2',expected:world}))return response({record:{requestId:'request-2',kind:'resume',expected:world,phase:'uncertain',nativeGeneration:'0'},state,error:{code:'uncertain',detail:'Inspect request'}},503);
   return response({record:{requestId:'request-4',kind:'resume',expected:world,phase:'running',nativeGeneration:'4'},state,error:null});
  }
  if(url==='/api/player/control/pause'){latest={record:{requestId:'request-3',kind:'pause',expected:world,phase:'paused',nativeGeneration:'0'},state,error:null};return response(latest);}
  throw Error(url);
 },()=>latest);
 await act(async()=>{render(<PlayerControls observation={observation} observationFresh/>);});fill();await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Resume'}));});
 expect(screen.getByRole('button',{name:'Resume'})).toBeDisabled();
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Pause'}));});
 expect(screen.getByRole('button',{name:'Resume'})).toBeDisabled();
 await act(async()=>{await vi.advanceTimersByTimeAsync(1500);});expect(screen.getByRole('button',{name:'Resume'})).toBeEnabled();
 expect(fetcher.mock.calls.filter(([url])=>url==='/api/player/control/resume')).toHaveLength(1);
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Resume'}));});
 expect(fetcher).toHaveBeenCalledWith('/api/player/control/resume',expect.objectContaining({body:JSON.stringify({requestId:'request-4',expected:world})}));
 expect(screen.getByText('request-2')).toBeVisible();expect(screen.getByText(/Previous resume request:/)).toHaveTextContent('Historical result: uncertain');
});

it.each([[409,'conflict'],[403,'player_auth']] as const)('releases submission after definite %s rejection only for a new explicit POST',async(status,code)=>{
 let posts=0;
 const fetcher=setup(async(url,options)=>{if(url==='/api/buildings/plans'){posts++;if(posts===1)return response({code,detail:'Not admitted'},status);const request=JSON.parse(options.body as string) as Record<string, unknown>;return response({...request,planId:'plan',actionId:'action',revision:'1'},201);}throw Error(url);});
 await act(async()=>{render(<PlayerControls observation={observation} observationFresh/>);});fill();
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 expect(posts).toBe(1);expect(screen.getByRole('button',{name:'Submit building plan'})).toBeEnabled();expect(screen.getByText(/Rejected before admission/)).toBeVisible();
 fireEvent.change(screen.getByLabelText('Definition name'),{target:{value:'Door'}});
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 expect(posts).toBe(2);expect(screen.getByText('request-1')).toBeVisible();expect(screen.getByText('Plan plan · Revision 1')).toBeVisible();
 expect(fetcher.mock.calls.filter(([,options])=>options?.method==='POST')[1][1]?.body).toContain('"requestId":"request-2"');
});
it('releases a no-record Resume conflict after refreshing current control',async()=>{
 vi.useFakeTimers();let posts=0;
 const latest={record:{requestId:'other',kind:'pause',expected:world,phase:'paused',nativeGeneration:'0'},state,error:null};
 let next:unknown=current;
 const fetcher=setup(async(url)=>{if(url==='/api/buildings/plans')return response({requestId:'request-1',expected:world,building,planId:'plan',actionId:'action',revision:'1'});if(url==='/api/player/control/resume'){posts++;next=latest;return response({record:null,state,error:{code:'conflict',detail:'CAS changed'}},409);}throw Error(url);},()=>next);
 await act(async()=>{render(<PlayerControls observation={observation} observationFresh/>);});fill();await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Resume'}));});
 expect(posts).toBe(1);expect(screen.getByRole('button',{name:'Resume'})).toBeDisabled();
 await act(async()=>{await vi.advanceTimersByTimeAsync(1500);});expect(screen.getByRole('button',{name:'Resume'})).toBeEnabled();expect(posts).toBe(1);
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Resume'}));});
 expect(posts).toBe(2);expect(fetcher.mock.calls.filter(([url])=>url==='/api/player/control/resume')[1][1]?.body).toContain('"requestId":"request-3"');
});
it.each(['transport','malformed','unavailable'])('keeps %s submission outcome frozen even after lookup404',async(kind)=>{
 const fetcher=setup(async(url)=>{if(url==='/api/buildings/plans'){if(kind==='transport')throw Error('Lost reply');return kind==='malformed'?response({code:'conflict',detail:'Bad',extra:true},409):response({code:'unavailable',detail:'Check request'},503);}return response({code:'not_found',detail:'No row yet'},404);});
 await act(async()=>{render(<PlayerControls observation={observation} observationFresh/>);});fill();await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Check submission result'}));});
 expect(screen.getByRole('button',{name:'Submit building plan'})).toBeDisabled();expect(fetcher.mock.calls.filter(([,options])=>options?.method==='POST')).toHaveLength(1);
});
it('shows initial bootstrap failure and retries without sending a POST',async()=>{
 vi.useFakeTimers();let calls=0;
 const fetcher=vi.fn(async(_url:string,_options:RequestInit={})=>{calls++;return response({code:'unavailable',detail:'Service unavailable'},503);});vi.stubGlobal('fetch',fetcher);
 await act(async()=>{render(<PlayerControls observation={observation} observationFresh/>);});
 expect(screen.getByRole('alert')).toHaveTextContent('Player controls unavailable');
 await act(async()=>{await vi.advanceTimersByTimeAsync(1500);});expect(calls).toBe(2);expect(fetcher.mock.calls.every(([,options])=>options?.method==='GET')).toBe(true);
});

it('submits a chat message, shows the adviser reply with its applied guidance and resumes the bot',async()=>{
 const fetcher=setup(async(url,options)=>{
  if(url==='/api/chat')return response({requestId:'request-1',expected:world,explanation:'Capping the colony at eight.',guidance:{kind:'set_population_policy',populationPolicy:{maximum:8,foodDays:20}}},201);
  if(url==='/api/player/control/resume')return response({record:{requestId:'request-2',kind:'resume',expected:world,phase:'running',nativeGeneration:'2'},state,error:null});
  throw Error(url+JSON.stringify(options));
 });
 await act(async()=>{render(<PlayerControls observation={observation} observationFresh/>);});
 fireEvent.change(screen.getByLabelText('Message'),{target:{value:'keep the colony small'}});
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Send'}));});
 expect(screen.getByText('Capping the colony at eight.')).toBeVisible();expect(screen.getByText('Applied: Population policy: up to 8 colonists, 20 food days')).toBeVisible();
 expect(fetcher).toHaveBeenCalledWith('/api/chat',expect.objectContaining({body:JSON.stringify({requestId:'request-1',expected:world,message:'keep the colony small'})}));
 expect(screen.getByLabelText('Message')).toHaveValue('');
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Resume'}));});
 expect(fetcher).toHaveBeenCalledWith('/api/player/control/resume',expect.objectContaining({body:JSON.stringify({requestId:'request-2',expected:world})}));
});
it('hides chat once the server reports it disabled, without disturbing other forms',async()=>{
 const fetcher=setup(async url=>{if(url==='/api/chat')return response({code:'unsupported',detail:'Chat is not enabled on this controller'},501);throw Error(url);});
 await act(async()=>{render(<PlayerControls observation={observation} observationFresh/>);});
 expect(screen.getByText('Chat')).toBeVisible();
 fireEvent.change(screen.getByLabelText('Message'),{target:{value:'why is nobody cooking?'}});
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Send'}));});
 expect(screen.queryByText('Chat')).toBeNull();expect(screen.getByLabelText('Definition name')).toHaveValue('');
 expect(fetcher.mock.calls.filter(([url])=>url==='/api/chat')).toHaveLength(1);
});
