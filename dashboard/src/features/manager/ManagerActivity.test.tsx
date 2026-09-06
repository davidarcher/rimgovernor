import {afterEach,expect,it,vi} from 'vitest';
import {cleanup,fireEvent,render,screen} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import ManagerActivity from './ManagerActivity';
afterEach(()=>{cleanup();vi.unstubAllGlobals();});
function mock(events:any[]){vi.stubGlobal('fetch',vi.fn().mockResolvedValue({ok:true,json:async()=>({colony:'one',events})}));}
it('defaults to outcomes and collapses inspections and duplicate diagnostic errors into accurate counts',async()=>{
 mock([
 {id:6,at:1,kind:'execution',role:'Executor:construction',outcomes:{issued:1},results:[{id:'w',title:'Three bed blueprints',status:'issued'}]},
 {id:5,at:1,kind:'action',role:'Executor:construction',endpoint:'/place',text:'Three bed blueprints'},
 {id:4,at:1,kind:'tool_result',role:'Infrastructure: construction',tool:'query',result:{error:'Wrong ID'}},
 {id:3,at:1,kind:'model_diagnostic',role:'Executor:construction',call:{},error:'Wrong ID'},
 {id:2,at:1,kind:'tool_result',role:'Infrastructure: construction',tool:'construction_definitions',arguments:{search:'Wall'},result:{total:1}},
 {id:1,at:1,kind:'proposal',role:'Infrastructure',text:'Draft shelter proposal'}]);
 render(<ManagerActivity colony="one"/>);
 expect(await screen.findByText('Three bed blueprints — Sent; awaiting verification')).toBeVisible();
 expect(screen.getByText(/1 commands sent · 2 tool calls · 1 corrections/)).toBeVisible();
 expect(screen.queryByText('Draft shelter proposal')).not.toBeInTheDocument();
 expect(screen.queryByText(/Building search/)).not.toBeInTheDocument();
 fireEvent.click(screen.getByText(/1 commands sent/));
 expect(await screen.findByText('Building search · “Wall” · 1 matches')).toBeVisible();
});
it('retains blockers, distinguishes planned layout from verified outcomes, and resets on colony change',async()=>{
 mock([{id:3,at:1,kind:'spatial_plan',role:'Architect',text:'Reserved the kitchen site.'},{id:2,at:1,kind:'work_outcome',role:'Executor:construction',results:[{id:'bed',title:'Bed',status:'complete'}]},{id:1,at:1,kind:'error',role:'Survival',text:'No suitable growing land.'}]);
 const view=render(<ManagerActivity colony="one"/>);
 expect(await screen.findByText('Bed — Verified')).toBeVisible();
 expect(screen.getByText('Layout planned')).toBeVisible();
 expect(screen.getByText('No suitable growing land.')).toBeVisible();
 fireEvent.change(screen.getByLabelText('Activity source'),{target:{value:'Architect'}});
 expect(screen.queryByText('Bed — Verified')).not.toBeInTheDocument();
 expect(screen.getByText('Reserved the kitchen site.')).toBeVisible();
 view.rerender(<ManagerActivity colony="two"/>);
 expect(screen.queryByText('Reserved the kitchen site.')).not.toBeInTheDocument();
});
it('never promotes drafts or historical model prose to a verified result',async()=>{
 mock([{id:1,at:1,kind:'execution',role:'Executor',orders:2,text:'Built all beds.'},{id:2,at:1,kind:'execution',role:'Executor',outcomes:{},text:'No new orders recorded.'}]);
 render(<ManagerActivity colony="one"/>);
 expect(await screen.findByText('No outcomes recorded yet.')).toBeVisible();
 expect(screen.queryByText('Built all beds.')).not.toBeInTheDocument();
 expect(screen.queryByText('No new orders recorded.')).not.toBeInTheDocument();
});
