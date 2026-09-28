import {expect, it} from 'vitest';
import {readPlan} from './observationData';
const progress={stage:'completed',attempt:'1',tick:10,unresolved:false,receipt:'accepted',effect:'completed',unsuccessfulReason:null};
const action={id:'a',kind:'owned_draft',draft:{pawnId:'Pawn_42'},progress};
const plan=(value:unknown)=>({id:'p',revision:'1',actions:[value]});
it('reads a draft action',()=>{
 expect(readPlan(plan(action)).actions[0]).toEqual(action);
});
it('rejects mismatched action payload arms',()=>{
 for(const value of [
 {...action,building:{}},{...action,kind:'building'},{...action,draft:{pawnId:'Pawn_42',claimId:'private'}},
 ])expect(()=>readPlan(plan(value))).toThrow();
});
