import {expect, it} from 'vitest';
import {cleanupStages, readPlan} from './observationData';
const progress={stage:'completed',attempt:'1',tick:10,unresolved:false,receipt:'accepted',effect:'completed',unsuccessfulReason:null};
const action={id:'a',kind:'owned_draft',draft:{pawnId:'Pawn_42'},progress};
const plan=(value:unknown)=>({id:'p',revision:'1',actions:[value]});
it('preserves absent cleanup and all seven independent cleanup states',()=>{
 expect(readPlan(plan(action)).actions[0].progress).not.toHaveProperty('draftCleanup');
 for(const stage of cleanupStages){const parsed=readPlan(plan({...action,progress:{...progress,draftCleanup:{stage}}}));expect(parsed.actions[0].progress.draftCleanup).toEqual({stage});expect(parsed.actions[0].progress.stage).toBe('completed');}
});
it('rejects invented or null cleanup and mismatched action payload arms',()=>{
 for(const value of [
 {...action,building:{}},{...action,kind:'building'},{...action,draft:{pawnId:'Pawn_42',claimId:'private'}},
 ...[null,{stage:'unknown'},{stage:'released',claim:'private'}].map(draftCleanup=>({...action,progress:{...progress,draftCleanup}})),
 {id:'b',kind:'building',building:{defName:'Wall',stuff:'',x:0,z:0,rotation:'north'},progress:{...progress,draftCleanup:{stage:'released'}}}
 ])expect(()=>readPlan(plan(value))).toThrow();
});
