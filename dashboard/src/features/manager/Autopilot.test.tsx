// @vitest-environment jsdom
import {render,screen,fireEvent,waitFor,cleanup} from '@testing-library/react';
import {afterEach,it,expect,vi} from 'vitest';
import Autopilot,{type AutopilotSettings} from './Autopilot';

const settings:AutopilotSettings={version:'a'.repeat(64),source:'Defaults',values:{food_min_days:3,food_target_days:7,execution_speed:'Normal',foothold_food_days:3},defaults:{food_min_days:3,food_target_days:7,execution_speed:'Normal'},fields:[
 {key:'food_min_days',group:'Food',label:'Replenish below',unit:'days',help:'Start replenishing.',editable:true},
 {key:'food_target_days',group:'Food',label:'Food target',unit:'days',help:'Desired stock runway.',editable:true},
 {key:'execution_speed',group:'Operation',label:'Game speed',unit:'',help:'Native speed.',editable:true},
 {key:'foothold_food_days',group:'Verification',label:'Minimum foothold food',unit:'days',help:'Verification rule.',editable:false}]};
const props={settings,sessionId:'colony-a',connected:true,mode:'automate',onSaved:vi.fn()};
afterEach(()=>{cleanup();vi.unstubAllGlobals();vi.clearAllMocks();});

it('shows capacity deferrals and saves an integer development limit through the shared settings API',async()=>{
 const configured={...settings,values:{...settings.values,max_development_projects:2},fields:[...settings.fields,
  {key:'max_development_projects',group:'Development',label:'Concurrent projects',unit:'projects',help:'Limit new projects.',editable:true}]};
 const fetch=vi.fn().mockResolvedValue({ok:true,json:async()=>({...configured,values:{...configured.values,max_development_projects:1}})});
 vi.stubGlobal('fetch',fetch);
 render(<Autopilot {...props} settings={configured} plan={{revision:0,rationale:'',goals:[],constraints:[],risks:[],steps:[],controller:{development:{capacity:2,available_workers:3,committed:['MaintainWood'],goals:{EnsureFoodStorage:{score:100,selected:false,reason:'Development capacity committed to earlier projects',committed:false}}}}}}/>);
 expect(screen.getByText('1 committed projects · capacity 2 · 3 available workers')).toBeTruthy();
 expect(screen.getByText(/Development capacity committed to earlier projects/)).toBeTruthy();
 const limit=screen.getByLabelText('Development Concurrent projects') as HTMLInputElement;
 expect(limit.step).toBe('1');
 fireEvent.change(limit,{target:{value:'1'}});
 fireEvent.click(screen.getByRole('button',{name:'Save settings'}));
 await screen.findByText('Settings saved. Autopilot will use them on its next review.');
 expect(JSON.parse(fetch.mock.calls[0][1].body).changes).toEqual({max_development_projects:1});
});

it('preserves edited values across polling and refuses stale settings until reloaded',async()=>{
 const view=render(<Autopilot {...props}/>);
 const target=screen.getByLabelText('Food Food target') as HTMLInputElement;
 await waitFor(()=>expect(target.value).toBe('7'));
 fireEvent.change(target,{target:{value:'20'}});
 view.rerender(<Autopilot {...props} settings={{...settings}}/>);
 expect(target.value).toBe('20');
 view.rerender(<Autopilot {...props} settings={{...settings,version:'b'.repeat(64),values:{...settings.values,food_target_days:10}}}/>);
 expect(target.value).toBe('20');
 expect(screen.getByRole('alert').textContent).toContain('Your draft is preserved');
 expect((screen.getByRole('button',{name:'Save settings'}) as HTMLButtonElement).disabled).toBe(true);
 fireEvent.click(screen.getByRole('button',{name:'Reload current settings'}));
 expect(target.value).toBe('10');
});

it('sends a versioned semantic patch and displays rejected writes without discarding the draft',async()=>{
 const fetch=vi.fn().mockResolvedValueOnce({ok:false,json:async()=>({detail:'Food entry threshold must be below its recovery target'})}).mockResolvedValueOnce({ok:true,json:async()=>({...settings,version:'b'.repeat(64),values:{...settings.values,food_target_days:20}})});
 vi.stubGlobal('fetch',fetch);
 render(<Autopilot {...props}/>);
 const target=screen.getByLabelText('Food Food target') as HTMLInputElement;
 fireEvent.change(target,{target:{value:'20'}});
 fireEvent.click(screen.getByRole('button',{name:'Save settings'}));
 await screen.findByText('Food entry threshold must be below its recovery target');
 expect(target.value).toBe('20');
 fireEvent.click(screen.getByRole('button',{name:'Save settings'}));
 await screen.findByText('Settings saved. Autopilot will use them on its next review.');
 expect(JSON.parse(fetch.mock.calls[1][1].body)).toEqual({session_id:'colony-a',expected_version:'a'.repeat(64),changes:{food_target_days:20}});
 expect(props.onSaved).toHaveBeenCalledOnce();
 expect(screen.queryByLabelText('Verification Minimum foothold food')).toBeNull();
});
