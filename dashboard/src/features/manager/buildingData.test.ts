import {afterEach, expect, it, vi} from 'vitest';
import {acquireBuilding, readBuilding, readControl, readPlayerSession, readSubmission, type ControlReply} from './buildingData';
const world = {colonyId:'colony',mapId:0,loadToken:'load'};
const building = {defName:'Wall',stuff:'WoodLog',x:1,z:2,rotation:'north'};
const record = {requestId:'acquire',kind:'acquire',expected:world,planId:'plan',revision:'9007199254740993',expectedDirection:'18446744073709551614',direction:'18446744073709551615',phase:'granted',nativeGeneration:'2'};
const reply = {record,state:{enabled:false,observationKnown:false,generation:null},error:null};
afterEach(()=>vi.unstubAllGlobals());
it('keeps uint64 strings and historical grants separate from actual permission',()=>{
 const value=readControl(reply);expect(value.record?.direction).toBe('18446744073709551615');expect(value.state.enabled).toBe(false);
 expect(readSubmission({requestId:'submit',expected:world,building,planId:'plan',actionId:'action',revision:record.revision}).revision).toBe(record.revision);
});
it('rejects unknown fields, missing facts and invalid typed variants',()=>{
 for(const value of [{...reply,extra:1},{...reply,state:{enabled:true,observationKnown:false,generation:null}},{...reply,record:{...record,direction:'18446744073709551616'}},{...reply,record:{...record,direction:1}},{...reply,record:{...record,phase:'invented'}},{...reply,record:{...record,phase:'uncertain'}},{...reply,record:{...record,kind:'manual'}}]) expect(()=>readControl(value)).toThrow();
 for(const value of [{...building,x:null},{...building,x:2147483648},{...building,x:1.2},{...building,defName:'\0Wall'},{...building,defName:'😀'.repeat(65)},{...building,defName:'\ud800'},{...building,rotation:'random'},{...building,stuff:undefined}]) expect(()=>readBuilding(value)).toThrow();
 expect(readBuilding({...building,stuff:''}).stuff).toBe('');
});
it('retains a typed uncertain record on HTTP503 and sends the exact token/CAS',async()=>{
 const uncertain: ControlReply=readControl({...reply,record:{...record,phase:'uncertain',nativeGeneration:'0'},error:{code:'uncertain',detail:'Inspect request'}});
 const fetcher=vi.fn(async()=>new Response(JSON.stringify(uncertain),{status:503}));vi.stubGlobal('fetch',fetcher);
 const request={requestId:'acquire',expected:world,planId:'plan',revision:record.revision,expectedDirection:record.expectedDirection};
 expect(await acquireBuilding('secret',request)).toEqual(uncertain);
 const options=fetcher.mock.calls[0];expect(options).toBeDefined();
 expect(fetcher).toHaveBeenCalledWith('/api/buildings/control/acquire',expect.objectContaining({method:'POST',headers:{'Content-Type':'application/json','X-RimGovernor-Player':'secret'},body:JSON.stringify(request)}));
});
it('treats bootstrap404 as read-only',async()=>{vi.stubGlobal('fetch',vi.fn(async()=>new Response(JSON.stringify({code:'not_found',detail:'Missing'}),{status:404})));expect(await readPlayerSession()).toBeNull();});
