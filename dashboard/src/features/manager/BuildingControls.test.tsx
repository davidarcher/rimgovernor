// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import {act, cleanup, fireEvent, render, screen} from '@testing-library/react';
import {afterEach, expect, it, vi} from 'vitest';
import BuildingControls from './BuildingControls';
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
 const fetcher=vi.fn((url:string,options:RequestInit={})=> url==='/api/buildings/session'?Promise.resolve(response({token:'secret',mode:'explicit-player'})):url==='/api/buildings/control'?Promise.resolve(response(readControl())):handler(url,options));
 vi.stubGlobal('fetch',fetcher);return fetcher;
}
afterEach(()=>{cleanup();vi.useRealTimers();vi.unstubAllGlobals();});
it('hides controls on read-only bootstrap',async()=>{vi.stubGlobal('fetch',vi.fn(async()=>response({code:'not_found',detail:'Missing'},404)));await act(async()=>{render(<BuildingControls observation={observation} observationFresh/>);});expect(screen.queryByRole('button')).toBeNull();});
it('submits an immutable explicit plan without acquiring and preserves editable draft',async()=>{
 const fetcher=setup(async(url)=>{if(url==='/api/buildings/plans')return response({requestId:'request-1',expected:world,building,planId:'plan',actionId:'action',revision:'9007199254740993'},201);throw Error(url);});
 const view=render(<BuildingControls observation={observation} observationFresh/>);await act(async()=>{});fill();
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 expect(screen.getByText('Plan plan · Revision 9007199254740993')).toBeVisible();expect(screen.getByLabelText('Definition name')).toHaveValue('Wall');
 expect(fetcher).toHaveBeenCalledWith('/api/buildings/plans',expect.objectContaining({headers:{'Content-Type':'application/json','X-RimGovernor-Player':'secret'},body:JSON.stringify({requestId:'request-1',expected:world,building})}));
 expect(fetcher.mock.calls.filter(([url])=>url.includes('/acquire'))).toHaveLength(0);
 view.rerender(<BuildingControls observation={{...observation,connected:false,game:{...observation.game,stale:true}}} observationFresh={false}/>);
 expect(screen.getByLabelText('Definition name')).toHaveValue('Wall');expect(screen.getByRole('button',{name:'Enable this plan'})).toBeDisabled();expect(screen.getByRole('button',{name:'Manual — stop orders'})).toBeEnabled();
});
it('recovers a lost submission through GET without another POST or request ID',async()=>{
 const fetcher=setup(async(url)=>{if(url==='/api/buildings/plans')throw Error('Lost response');if(url==='/api/buildings/submission?requestId=request-1')return response({requestId:'request-1',expected:world,building,planId:'plan',actionId:'action',revision:'1'});throw Error(url);});
 await act(async()=>{render(<BuildingControls observation={observation} observationFresh/>);});fill();
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 expect(screen.getByRole('button',{name:'Submit building plan'})).toBeDisabled();
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Check submission result'}));});
 expect(screen.getByText('Plan plan · Revision 1')).toBeVisible();expect(fetcher.mock.calls.filter(([,options])=>options?.method==='POST')).toHaveLength(1);
});
it('keeps Manual independent of pending Acquire and ignores its late permission state',async()=>{
 let finish:((response:Response)=>void)|undefined;
 const fetcher=setup(async(url)=>{
  if(url==='/api/buildings/plans')return response({requestId:'request-1',expected:world,building,planId:'plan',actionId:'action',revision:'1'});
  if(url==='/api/buildings/control/acquire')return new Promise<Response>(resolve=>{finish=resolve;});
  if(url==='/api/buildings/control/manual')return response({record:{requestId:'request-3',kind:'manual',expected:world,planId:null,revision:'0',expectedDirection:'0',direction:'2',phase:'disabled',nativeGeneration:'0'},state,error:null});
  throw Error(url);
 });
 await act(async()=>{render(<BuildingControls observation={observation} observationFresh/>);});fill();await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Enable this plan'}));});
 expect(screen.getByRole('button',{name:'Manual — stop orders'})).toBeEnabled();
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Manual — stop orders'}));});
 expect(fetcher.mock.calls.filter(([url])=>url==='/api/buildings/control/manual')).toHaveLength(1);
 await act(async()=>{finish?.(response({record:{requestId:'request-2',kind:'acquire',expected:world,planId:'plan',revision:'1',expectedDirection:'0',direction:'1',phase:'granted',nativeGeneration:'2'},state:{enabled:true,observationKnown:true,generation:{colony:'colony',load:'load',map:0,direction:'1',plan:'plan',revision:'1',native:'2'}},error:null}));});
 expect(screen.queryByText('Current permission: orders enabled')).toBeNull();expect(screen.getByText(/Historical result: granted/)).toBeVisible();
});
it('refetches memory token after server session change without reposting pending intent',async()=>{
 const fetcher=setup(async()=>{throw Error('Disconnected');});
 const view=render(<BuildingControls observation={observation} observationFresh/>);await act(async()=>{});fill();await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 await act(async()=>{view.rerender(<BuildingControls observation={{...observation,sessionId:'new-server',identity:{...world,loadToken:'new-load'}}} observationFresh/>);});
 expect(fetcher.mock.calls.filter(([url])=>url==='/api/buildings/session')).toHaveLength(2);expect(fetcher.mock.calls.filter(([,options])=>options?.method==='POST')).toHaveLength(1);expect(screen.getByLabelText('Definition name')).toHaveValue('Wall');expect(screen.getByText('request-1')).toBeVisible();
});
it('sends uint64 CAS unchanged and retains503 uncertainty through read recovery',async()=>{
 const direction='18446744073709551614';
 const historical={record:{requestId:'previous',kind:'manual',expected:world,planId:null,revision:'0',expectedDirection:'0',direction,phase:'disabled',nativeGeneration:'0'},state,error:null};
 const uncertain={record:{requestId:'request-2',kind:'acquire',expected:world,planId:'plan',revision:'9007199254740993',expectedDirection:direction,direction:'18446744073709551615',phase:'uncertain',nativeGeneration:'0'},state,error:{code:'uncertain',detail:'Inspect this request'}};
 const fetcher=setup(async(url)=>{
  if(url==='/api/buildings/plans')return response({requestId:'request-1',expected:world,building,planId:'plan',actionId:'action',revision:'9007199254740993'});
  if(url==='/api/buildings/control/acquire')return response(uncertain,503);
  if(url==='/api/buildings/control?requestId=request-2')return response({...uncertain,error:null});
  throw Error(url);
 },()=>historical);
 await act(async()=>{render(<BuildingControls observation={observation} observationFresh/>);});fill();await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Enable this plan'}));});
 expect(fetcher).toHaveBeenCalledWith('/api/buildings/control/acquire',expect.objectContaining({body:JSON.stringify({requestId:'request-2',expected:world,planId:'plan',revision:'9007199254740993',expectedDirection:direction})}));
 expect(screen.getByText(/Historical result: uncertain/)).toBeVisible();expect(screen.getByRole('button',{name:'Enable this plan'})).toBeDisabled();
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Check acquire result'}));});
 expect(fetcher.mock.calls.filter(([url])=>url==='/api/buildings/control/acquire')).toHaveLength(1);expect(screen.queryByText('Current permission: orders enabled')).toBeNull();
});
it('rebootstraps after403 without automatically reposting the frozen request',async()=>{
 const fetcher=setup(async()=>response({code:'player_auth',detail:'Token expired'},403));
 await act(async()=>{render(<BuildingControls observation={observation} observationFresh/>);});fill();await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 expect(fetcher.mock.calls.filter(([url])=>url==='/api/buildings/session')).toHaveLength(2);expect(fetcher.mock.calls.filter(([,options])=>options?.method==='POST')).toHaveLength(1);
 expect(screen.getByText('request-1')).toBeVisible();expect(screen.getByLabelText('Definition name')).toHaveValue('Wall');
});
it('preserves draft and submission on malformed control refresh, and never stops on unmount',async()=>{
 vi.useFakeTimers();let malformed=false;
 const fetcher=setup(async()=>response({requestId:'request-1',expected:world,building,planId:'plan',actionId:'action',revision:'1'}),()=>malformed?{...current,state:{enabled:true,observationKnown:false,generation:null}}:current);
 const view=render(<BuildingControls observation={observation} observationFresh/>);await act(async()=>{});fill();await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 malformed=true;await act(async()=>{await vi.advanceTimersByTimeAsync(1500);});
 expect(screen.getByText('Plan plan · Revision 1')).toBeVisible();expect(screen.getByLabelText('Definition name')).toHaveValue('Wall');expect(screen.getByRole('button',{name:'Enable this plan'})).toBeDisabled();
 view.unmount();expect(fetcher.mock.calls.filter(([url])=>url==='/api/buildings/control/manual')).toHaveLength(0);
});
it('allows a new explicitAcquire after fresh later Manual, retaining the frozen uncertain request',async()=>{
 vi.useFakeTimers();let latest:unknown=current;
 const fetcher=setup(async(url,options)=>{
  if(url==='/api/buildings/plans')return response({requestId:'request-1',expected:world,building,planId:'plan',actionId:'action',revision:'1'});
  if(url==='/api/buildings/control/acquire'){
   if(options.body===JSON.stringify({requestId:'request-2',expected:world,planId:'plan',revision:'1',expectedDirection:'0'}))return response({record:{requestId:'request-2',kind:'acquire',expected:world,planId:'plan',revision:'1',expectedDirection:'0',direction:'1',phase:'uncertain',nativeGeneration:'0'},state,error:{code:'uncertain',detail:'Inspect request'}},503);
   return response({record:{requestId:'request-4',kind:'acquire',expected:world,planId:'plan',revision:'1',expectedDirection:'2',direction:'3',phase:'granted',nativeGeneration:'4'},state,error:null});
  }
  if(url==='/api/buildings/control/manual'){latest={record:{requestId:'request-3',kind:'manual',expected:world,planId:null,revision:'0',expectedDirection:'0',direction:'2',phase:'disabled',nativeGeneration:'0'},state,error:null};return response(latest);}
  throw Error(url);
 },()=>latest);
 await act(async()=>{render(<BuildingControls observation={observation} observationFresh/>);});fill();await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Submit building plan'}));});
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Enable this plan'}));});
 expect(screen.getByRole('button',{name:'Enable this plan'})).toBeDisabled();
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Manual — stop orders'}));});
 expect(screen.getByRole('button',{name:'Enable this plan'})).toBeDisabled();
 await act(async()=>{await vi.advanceTimersByTimeAsync(1500);});expect(screen.getByRole('button',{name:'Enable this plan'})).toBeEnabled();
 expect(fetcher.mock.calls.filter(([url])=>url==='/api/buildings/control/acquire')).toHaveLength(1);
 await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Enable this plan'}));});
 expect(fetcher).toHaveBeenCalledWith('/api/buildings/control/acquire',expect.objectContaining({body:JSON.stringify({requestId:'request-4',expected:world,planId:'plan',revision:'1',expectedDirection:'2'})}));
 expect(screen.getByText('request-2')).toBeVisible();expect(screen.getByText(/Previous acquire request:/)).toHaveTextContent('Historical result: uncertain');
});
