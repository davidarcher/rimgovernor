// @vitest-environment jsdom
import {render,screen,cleanup} from '@testing-library/react';
import {afterEach,it,expect,vi} from 'vitest';
import CommittedPlan from './CommittedPlan';
afterEach(cleanup);
it('distinguishes unfinished functional gates, player provenance and the actual blocker',()=>{
 render(<CommittedPlan onCancel={vi.fn()} plan={{revision:3,rationale:'Establish a safe base',goals:[],constraints:[],risks:[],
  controller:{status:'ESTABLISHING_FOOTHOLD',criteria:{shelter:true,food:false}},
  colonyGoals:{EnsureFoodSupply:{status:'blocked',priority_class:2,source:'PLAYER',method:'rice',reason:'No safe fertile site',cancelled:false}},steps:[]}}/>);
 expect(screen.getByText('No safe fertile site')).toBeTruthy();
 expect(screen.getByText('Player request · Essential')).toBeTruthy();
 expect(screen.getByText(/food: not yet verified/)).toBeTruthy();
 expect(screen.queryByText('Starter colony stable')).toBeNull();
});
