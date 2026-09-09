import {useEffect,useState} from 'react';
import type {Committed} from './CommittedPlan';
import './Autopilot.css';

export type AutopilotSettings={version:string;source:string;values:Record<string,number|string>;defaults:Record<string,number|string>;fields:{key:string;group:string;label:string;unit:string;help:string;editable:boolean}[]};
export const goalName=(id:string)=>(({AllowStartingSupplies:'Allow starting supplies',EnsureWorkAssignments:'Assign colony work',EnsureFoodSupply:'Maintain food supply',EnsureInitialShelter:'Establish shelter',EnsureTemperatureSafety:'Keep sleeping rooms comfortable',EnsureCooking:'Maintain cooking',EnsureFoodStorage:'Store food indoors',EnsureBasicDefense:'Equip defenders',MaintainWood:'Maintain wood supply',ActiveCombat:'Defend the colony',CriticalMedical:'Treat urgent injuries',RestoreWorkers:'Return defenders to work',ConfirmColonyNames:'Confirm colony names'} as Record<string,string>)[id]||id.replace(/([a-z])([A-Z])/g,'$1 $2').replace(/^intent-/,'').replaceAll('-',' '));
export const gateName=(id:string)=>(({sleeping:'Sleeping places',shelter:'Roofed shelter',food:'Food runway',production:'Growing capacity',storage:'Indoor food storage',cooking:'Usable cooking',temperature:'Sleeping temperature',power:'Required power',medical:'Medical safety',defense:'Equipped defenders',work:'Work coverage'} as Record<string,string>)[id]||id);
export function nextWork(plan?:Committed,mode?:string){
 if(mode!=='automate')return 'Autopilot is off. Chat can still answer questions and carry out explicit orders.';
 const goals=Object.entries(plan?.colonyGoals||{}).filter(([,g])=>!g.cancelled&&g.status!=='complete').sort((a,b)=>a[1].priority_class-b[1].priority_class);
 const blocked=goals.find(([,g])=>g.status==='blocked');
 if(blocked&&blocked[1].priority_class<2)return goalName(blocked[0])+': '+blocked[1].reason;
 if(plan?.controller?.status==='FOOTHOLD_STABLE')return 'Maintaining food, fuel, work assignments and safety.';
 const active=goals.find(([,g])=>g.status==='active');
 return active?goalName(active[0])+(active[1].reason?': '+active[1].reason:'. Watching native progress before choosing more work.'):'Inspecting colony needs and choosing the next action.';
}
const sourceName=(source:string)=>source==='PLAYER'?'Player':source==='LLM_ADVISOR'?'Advisor':'Autopilot';
const number=(value:unknown,digits=0)=>typeof value==='number'?value.toFixed(digits):'—';
export function ColonyReadings({plan}:{plan?:Committed}){
 const facts=plan?.controller?.facts;
 return <div className="autopilot-readings" aria-label="Current colony readings">
  <div><span>Food runway</span><strong>{number(facts?.foodRunwayDays,1)} <small>days</small></strong></div>
  <div><span>Available wood</span><strong>{number(facts?.resources?.WoodLog)}</strong></div>
  <div><span>Indoor sleeping</span><strong>{number(facts?.indoorSleepingCapacity)} <small>/ {number(facts?.colonists)}</small></strong></div>
  <div><span>Sleeping temperature</span><strong>{number(facts?.sleepingTemperatureMin,1)} <small>°C</small></strong></div>
 </div>;
}
export default function Autopilot({settings,plan,sessionId,connected,mode,onSaved}:{settings?:AutopilotSettings;plan?:Committed;sessionId:string;connected:boolean;mode:string;onSaved:(settings:AutopilotSettings)=>void}){
 const [draft,setDraft]=useState<Record<string,string>>({}),[base,setBase]=useState(''),[dirty,setDirty]=useState(false),[saving,setSaving]=useState(false),[error,setError]=useState(''),[message,setMessage]=useState('');
 useEffect(()=>{if(settings&&!dirty){setDraft(Object.fromEntries(Object.entries(settings.values).map(([k,v])=>[k,String(v)])));setBase(settings.version);}},[settings?.version,dirty]);
 if(!settings)return <section className="mgr-card bridge-detail"><h1>Autopilot</h1><p>Waiting for controller settings…</p></section>;
 const conflict=dirty&&settings.version!==base;
 const reload=()=>{setDraft(Object.fromEntries(Object.entries(settings.values).map(([k,v])=>[k,String(v)])));setBase(settings.version);setDirty(false);setError('');setMessage('Current settings loaded.');};
 async function save(){
  if(!settings)return;
  setSaving(true);setError('');setMessage('');
  try{
   const changes=Object.fromEntries(settings.fields.filter(f=>f.editable&&String(settings.values[f.key])!==draft[f.key]).map(f=>[f.key,f.key==='execution_speed'?draft[f.key]:Number(draft[f.key])]));
   if(!Object.keys(changes).length){setDirty(false);return;}
   const response=await fetch('/api/autopilot/settings',{method:'POST',headers:{'Content-Type':'application/json','X-RimBot':'1'},body:JSON.stringify({session_id:sessionId,expected_version:base,changes})});
   const data=await response.json();if(!response.ok)throw Error(typeof data.detail==='string'?data.detail:'Check the setting values and try again.');
   onSaved(data);setBase(data.version);setDirty(false);setMessage('Settings saved. Autopilot will use them on its next review.');
  }catch(e){setError(String(e).replace(/^Error: /,''));}finally{setSaving(false);}
 }
 const goals=Object.entries(plan?.colonyGoals||{}).sort((a,b)=>a[1].priority_class-b[1].priority_class);
 return <section className="autopilot-page">
  <header className="autopilot-heading"><div><p className="mgr-eyebrow">DETERMINISTIC COLONY CONTROL</p><h1>Autopilot</h1><p>Routine work follows these targets. Chat and this panel change the same colony plan.</p></div><span className="mgr-pill">{mode==='automate'?'Running':'Manual'}</span></header>
  <ColonyReadings plan={plan}/>
  <div className="autopilot-layout"><div>
   <section className="mgr-card autopilot-section"><h2>What happens next</h2><p>{nextWork(plan,mode)}</p>{plan?.controller?.execution_hold&&<p role="status" className="autopilot-notice">{plan.controller.execution_hold}</p>}
    <div className="autopilot-gates">{Object.entries(plan?.controller?.criteria||{}).map(([key,ok])=><div key={key} className={ok?'verified':'pending'}><span aria-hidden="true">{ok?'✓':'○'}</span>{gateName(key)}<small>{ok?'Verified':'Needs attention'}</small></div>)}</div>
    {!plan?.controller?.criteria&&<p className="mgr-muted">Checks appear after the first autonomous review.</p>}
   </section>
   <section className="mgr-card autopilot-section"><h2>Goals and blockers</h2>{goals.length?goals.map(([id,g])=><article className="autopilot-goal" key={id}><header><b>{goalName(id)}</b><span className="mgr-pill">{g.cancelled?'Cancelled':g.status}</span></header><p>{g.reason||(g.status==='complete'?'The goal’s native postconditions are satisfied.':g.method?'Accepted work is being tracked.':'Waiting for an applicable action.')}</p><small>{sourceName(g.source)} · {['Emergency','Urgent care','Essential','Development','Optional'][g.priority_class]||'Routine'}</small></article>):<p>Turn on Automate to establish routine goals, or create one in chat.</p>}</section>
   <section className="mgr-card autopilot-section"><h2>Resource restrictions</h2>{Object.keys(plan?.controller?.resource_policy||{}).length?<ul>{Object.entries(plan?.controller?.resource_policy||{}).map(([key,p])=><li key={key}>{key==='ComponentIndustrial'?'Components':key}: {p.spending==='stop'?'No new spending':p.spending==='defense_only'?'Defense only':'Normal spending'} · reserve {p.reserve}</li>)}</ul>:<p>No additional resource restrictions.</p>}<p className="mgr-muted">Use chat to set resource restrictions. They govern new controller orders; existing production bills remain active.</p></section>
  </div>
  <form className="mgr-card autopilot-section autopilot-settings" onSubmit={e=>{e.preventDefault();save();}}><h2>Targets and settings</h2><p className="mgr-muted">Edits apply to this colony. Unsaved values stay here while the page refreshes.</p>
   {conflict&&<p role="alert" className="autopilot-notice">Settings changed in chat or another view. Your draft is preserved; reload current settings before saving.</p>}
   {['Operation','Food','Wood','Temperature'].map(group=><fieldset key={group}><legend>{group}</legend>{settings.fields.filter(f=>f.group===group).map(f=><div className="autopilot-field" key={f.key}><label htmlFor={'policy-'+f.key}>{f.label} {f.unit&&<small>({f.unit})</small>}</label>{f.key==='execution_speed'?<select id={'policy-'+f.key} aria-label={group+' '+f.label} value={draft[f.key]||''} disabled={saving} onChange={e=>{setDraft({...draft,[f.key]:e.target.value});setDirty(true);setMessage('');}}>{['Normal','Fast','Superfast'].map(v=><option key={v}>{v}</option>)}</select>:<input required id={'policy-'+f.key} aria-label={group+' '+f.label} type="number" step={f.key.startsWith('wood_')?1:.1} value={draft[f.key]??''} disabled={saving} onChange={e=>{setDraft({...draft,[f.key]:e.target.value});setDirty(true);setMessage('');}}/>}<small className="mgr-muted">{f.help}</small></div>)}</fieldset>)}
   {error&&<p role="alert" className="autopilot-notice">{error}</p>}{message&&<p role="status">{message}</p>}
   <div className="autopilot-save"><button className="mgr-primary" disabled={!dirty||saving||conflict||!connected}>{saving?'Saving…':'Save settings'}</button><button type="button" onClick={reload} disabled={saving}>Reload current settings</button><span>{dirty?'Unsaved changes':'Saved values'}</span></div>
   <details className="autopilot-verification"><summary>Verification and recovery limits</summary><p className="mgr-muted">These are controller safeguards, shown for transparency.</p><dl>{settings.fields.filter(f=>!f.editable).map(f=><div key={f.key}><dt>{f.label}</dt><dd>{settings.values[f.key]} {f.unit}</dd><small>{f.help}</small></div>)}</dl></details>
  </form></div>
 </section>;
}
