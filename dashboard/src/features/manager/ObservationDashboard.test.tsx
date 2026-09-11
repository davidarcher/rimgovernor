// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import {act,cleanup,render,screen} from '@testing-library/react';
import {afterEach,expect,it,vi} from 'vitest';
import ObservationDashboard from './ObservationDashboard';
import {readObservation,readPlan} from './observationData';
const state={sessionId:'load-a',connected:true,mode:'manual',status:{label:'Colony observed'},identity:{colonyId:'colony-a',mapId:0,loadToken:'load-a'},game:{tick:42,paused:false,observedAt:'2026-09-10T12:00:00Z',stale:false},activePlanId:'plan-a'};
const plan={id:'plan-a',revision:'9007199254740993',actions:[{id:'action-a',kind:'building',building:{defName:'Wall',x:2,z:3,rotation:'north',stuff:'Granite'},progress:{stage:'awaiting_observation',attempt:'1',tick:40,unresolved:true,receipt:'accepted',effect:null,unsuccessfulReason:null}}]};
const reply=(value:unknown)=>({ok:true,json:async()=>value});
afterEach(()=>{cleanup();vi.useRealTimers();vi.unstubAllGlobals();});
it('renders observed native facts and typed plan with no mutation controls',async()=>{
 const fetcher=vi.fn(async(url:string)=>reply(url==='/api/state'?state:plan));vi.stubGlobal('fetch',fetcher);
 await act(async()=>{render(<ObservationDashboard/>);});
 expect(screen.getByText('RimGovernor')).toBeVisible();expect(screen.getByText('Observation mode')).toBeVisible();expect(screen.getByText('Running')).toBeVisible();
 expect(screen.getByText('colony-a')).toBeVisible();expect(screen.getByText('Wall')).toBeVisible();expect(screen.getByText('Outcome requires observation')).toBeVisible();
 expect(screen.getByText('plan-a · Revision 9007199254740993')).toBeVisible();expect(screen.queryByRole('button')).toBeNull();expect(screen.queryByRole('textbox')).toBeNull();
 expect(fetcher.mock.calls.map(call=>call[0])).toEqual(['/api/state','/api/plan?id=plan-a']);
});
it('keeps last good readings and plan on refresh errors',async()=>{
 vi.useFakeTimers();let fail=false;
 vi.stubGlobal('fetch',vi.fn(async(url:string)=>{if(fail)throw Error('Offline');return reply(url==='/api/state'?state:plan);}));
 await act(async()=>{render(<ObservationDashboard/>);});fail=true;
 await act(async()=>{await vi.advanceTimersByTimeAsync(1500);});
 expect(screen.getByText('Wall')).toBeVisible();expect(screen.getByText('Tick 42')).toBeVisible();expect(screen.getByRole('status')).toHaveTextContent('Offline');
 expect(screen.getByText('Readings unavailable or stale')).toBeVisible();
});
it('clears a previous plan when native identity changes before a failed plan refresh',async()=>{
 vi.useFakeTimers();let changed=false;
 vi.stubGlobal('fetch',vi.fn(async(url:string)=>{if(url==='/api/state')return reply(changed?{...state,sessionId:'load-b',identity:{...state.identity,loadToken:'load-b'}}:state);if(changed)throw Error('Plan unavailable');return reply(plan);}));
 await act(async()=>{render(<ObservationDashboard/>);});changed=true;
 await act(async()=>{await vi.advanceTimersByTimeAsync(1500);});
 expect(screen.queryByText('Wall')).toBeNull();expect(screen.getByText('load-b')).toBeVisible();expect(screen.getByText('Waiting for the active plan.')).toBeVisible();
});
it('shows unknown observations explicitly and cancels its requests on unmount',async()=>{
 let signal:AbortSignal|undefined;
 vi.stubGlobal('fetch',vi.fn(async(_url:string,options:{signal:AbortSignal})=>{signal=options.signal;return reply({...state,identity:null,activePlanId:null,game:{tick:null,paused:null,observedAt:null,stale:true}});}));
 let view:ReturnType<typeof render>|undefined;await act(async()=>{view=render(<ObservationDashboard/>);});
 expect(screen.getByText('Tick unknown')).toBeVisible();expect(screen.getAllByText('Unknown').length).toBeGreaterThan(1);expect(screen.getByText('No active plan reported.')).toBeVisible();
 view?.unmount();expect(signal?.aborted).toBe(true);
});
it('rejects malformed wire values instead of turning unknown values into facts',()=>{
 expect(()=>readObservation({...state,game:{...state.game,paused:0}})).toThrow();
 expect(()=>readObservation({...state,game:{...state.game,tick:Number.MAX_SAFE_INTEGER+1}})).toThrow();
 expect(()=>readPlan({...plan,actions:[{...plan.actions[0],kind:'tool'}]})).toThrow();
 expect(()=>readPlan({...plan,actions:[plan.actions[0],plan.actions[0]]})).toThrow();
});

function withProgress(progress: object) {return {...plan,actions:[{...plan.actions[0],progress:{...plan.actions[0].progress,...progress}}]};}
it('preserves null reasons and validates closed unsuccessful outcomes',()=>{
 expect(readPlan(plan).actions[0].progress.unsuccessfulReason).toBeNull();
 for(const reason of ['native_failure','cancelled','interrupted','expired','target_dead','outcome_not_achieved']) {
  const result=readPlan(withProgress({stage:'unsuccessful',effect:'unsuccessful',unresolved:false,unsuccessfulReason:reason}));
  expect(result.actions[0].progress.unsuccessfulReason).toBe(reason);
  expect(result.revision).toBe('9007199254740993');
 }
 for(const patch of [
  {stage:'unsuccessful',effect:'unsuccessful',unresolved:false,unsuccessfulReason:'invented'},
  {stage:'unsuccessful',effect:'unsuccessful',unresolved:false,unsuccessfulReason:null},
  {unsuccessfulReason:'target_dead'}, {unsuccessfulReason:undefined},
 ]) expect(()=>readPlan(withProgress(patch))).toThrow();
});
it.each(['cancelled','unsuccessful'])('shows unsuccessful evidence in %s stage and retains it on bad refresh',async(stage)=>{
 vi.useFakeTimers();let invalid=false;
 vi.stubGlobal('fetch',vi.fn(async(url:string)=>reply(url==='/api/state'?state:withProgress({stage,effect:'unsuccessful',unresolved:false,unsuccessfulReason:invalid?'invented':'outcome_not_achieved'}))));
 await act(async()=>{render(<ObservationDashboard/>);});
 expect(screen.getByText(stage==='cancelled'?'Cancelled':'Unsuccessful')).toBeVisible();
 expect(screen.getByText('Expected outcome not achieved')).toBeVisible();
 expect(screen.queryByText('Completion observed')).toBeNull();
 invalid=true;await act(async()=>{await vi.advanceTimersByTimeAsync(1500);});
 expect(screen.getByText('Expected outcome not achieved')).toBeVisible();
 expect(screen.getByRole('status')).toHaveTextContent('Unsupported response value');
 expect(screen.queryByRole('button')).toBeNull();
});
