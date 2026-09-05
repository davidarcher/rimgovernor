import {afterEach,expect,it,vi} from 'vitest';
import {cleanup,fireEvent,render,screen} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import ManagerActivity from './ManagerActivity';
afterEach(()=>{cleanup();vi.unstubAllGlobals();});
it('shows successful inspections and arbitration, filters roles, and clears another colony',async()=>{
  vi.stubGlobal('fetch',vi.fn().mockResolvedValue({ok:true,json:async()=>({colony:'one',events:[
    {id:3,at:1,kind:'model_call',role:'Infrastructure',seconds:2,tools:['query']},
    {id:2,at:1,kind:'arbitration',role:'Administrator',text:'Build the beds.',accepted:['Infrastructure'],deferred:{Survival:'Supplies already available'}},
    {id:1,at:1,kind:'tool_result',role:'Infrastructure',tool:'query',arguments:{endpoint:'get_map_buildings'},result:{total:3}}
  ]})}));
  const view=render(<ManagerActivity colony="one"/>);
  expect(await screen.findByText('Approved: Infrastructure')).toBeVisible();
  expect(screen.queryByText(/Review finished/)).not.toBeInTheDocument();
  expect(screen.getByText(/Inspected get map buildings/)).toBeVisible();
  fireEvent.click(screen.getByRole('button',{name:'Infrastructure'}));
  expect(screen.queryByText('Build the beds.')).not.toBeInTheDocument();
  expect(screen.getByText('Details · query')).toBeVisible();
  view.rerender(<ManagerActivity colony="two"/>);
  expect(screen.queryByText(/Inspected get map buildings/)).not.toBeInTheDocument();
});
