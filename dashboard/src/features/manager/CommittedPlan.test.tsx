// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import {render,screen,cleanup,fireEvent} from '@testing-library/react';
import {afterEach,it,expect,vi} from 'vitest';
import CommittedPlan from './CommittedPlan';
import FieldGuide from './FieldGuide';
import {readable} from './labels';

afterEach(cleanup);
it('shows blocked work by purpose and puts finished work behind a filter',()=>{
 const cancel=vi.fn();
 render(<CommittedPlan onCancel={cancel} plan={{revision:3,rationale:'Establish a safe base',goals:[],constraints:[],risks:[],
 colonyGoals:{EnsureFoodSupply:{status:'blocked',priority_class:2,source:'PLAYER',method:'rice',reason:'No safe fertile site',cancelled:false}},
 steps:[{id:'raw-step-1234',title:'EnsureFoodSupply: acquire-3053466a',state:'blocked',priority:2,source:'PLAYER',action:'native_operation',completion:'Waiting for food',issued:0,failure:{detail:'No safe fertile site'}},{id:'done-1234',title:'Make a sleeping spot',state:'complete',priority:2,action:'native_operation',completion:'Native spot verified',issued:1}]}}/>);
 expect(screen.getByText('No safe fertile site')).toBeVisible();
 expect(screen.getByText('Maintain food supply: gather supplies')).toBeVisible();
 expect(screen.queryByText('Make a sleeping spot')).toBeNull();
 fireEvent.click(screen.getByRole('button',{name:'Cancel'}));expect(cancel).toHaveBeenCalledWith('raw-step-1234');
 fireEvent.click(screen.getByLabelText('Include finished work'));
 expect(screen.getByText('Make a sleeping spot')).toBeVisible();
 expect(screen.getAllByRole('button',{name:'Cancel'})).toHaveLength(1);
});
it('explains goal reasons without presenting priority classes as invented scores',()=>{
 render(<FieldGuide plan={{revision:1,rationale:'',goals:[],constraints:[],risks:[],steps:[],colonyGoals:{CriticalMedical:{status:'blocked',priority_class:1,source:'PLAYER',method:'tend-Thing_Human1067',reason:'No available doctor',cancelled:false}}}}/>);
 fireEvent.click(screen.getByText('Treat urgent injuries'));
 expect(screen.getByText('No available doctor')).toBeVisible();
 expect(screen.getByText('You')).toBeVisible();
 expect(screen.getByText(/not probability scores/)).toBeVisible();
 expect(screen.getByText('Diagnostic evidence').closest('details')).not.toHaveAttribute('open');
});
it('resolves exact action and pawn identities before simplifying method hashes',()=>{
 expect(readable('Treat Thing_Human1067',undefined,[{thing_id:'Thing_Human1067',name:'Wobbler'}])).toBe('Treat Wobbler');
 expect(readable('AllowStartingSupplies: allow-71628c73',{revision:1,rationale:'',goals:[],constraints:[],risks:[],steps:[],colonyGoals:{AllowStartingSupplies:{status:'active',priority_class:2,source:'AUTOPILOT',method:'',reason:'',cancelled:false}}})).toBe('Allow starting supplies: make starting supplies available');
});
