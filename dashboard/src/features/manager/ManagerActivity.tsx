import {useEffect,useState} from 'react';
const source=(e:any)=>{
 const parts=(e.role||'Colony').split(':');
 if(parts.length>1&&(parts[0]==='Executor'||['construction','growing','storage','production','care','security','research','work_assignment','supply_access'].includes(parts[1].trim()))){const n=parts[1].trim().replaceAll('_',' ');return n.charAt(0).toUpperCase()+n.slice(1)+' specialist';}
 return parts[0]==='Executor'?'Specialist':parts[0];
};
const outcome=(e:any)=>['arbitration','spatial_plan','project_cancelled','work_outcome','error','escalation'].includes(e.kind)||(e.kind==='execution'&&(Object.values(e.outcomes||{}).some(n=>Number(n)>0)||e.blockers?.length));
function tally(events:any[]){
 const commands=events.filter(e=>e.kind==='action'&&e.endpoint).length;
 const tools=events.filter(e=>e.kind==='tool_result'&&e.tool!=='submit').length;
 const corrections=events.filter(e=>(e.kind==='tool_result'&&e.result?.error)||(e.kind==='model_diagnostic'&&!e.call&&(e.error||e.errors))).length;
 return `${commands} commands sent · ${tools} tool calls · ${corrections} corrections`;
}
function requestSummary(e:any){
  const a=e.arguments||{}, parts:string[]=[];
  if(a.search)parts.push(`“${a.search}”`);
  if(a.path)parts.push(a.path);
  if(a.where)parts.push(Object.entries(a.where).map(([k,v])=>`${k}=${typeof v==='object'?JSON.stringify(v):v}`).join(', '));
  const point=a.near||a.center||a.position;
  if(point?.x!=null&&point?.z!=null)parts.push(`near (${point.x}, ${point.z})`);
  if(a.radius!=null)parts.push(`radius ${a.radius}`);
  if(a.offset)parts.push(`offset ${a.offset}`);
  return parts.filter(Boolean).join(' · ');
}
function caption(e:any){
  if(e.kind==='execution'&&!e.outcomes&&e.orders!=null)return `Proposed ${e.orders} orders · historical planning summary; see Work for execution results`;
  if(e.kind==='escalation')return e.text||'Needs a decision';
  if(e.kind==='tool_result'){
    const detail=requestSummary(e), suffix=detail?` · ${detail}`:'';
    if(e.result?.error)return `${e.tool}${suffix} · Needs correction: ${e.result.error}`;
    if(e.result?.drafted)return `Drafted: ${e.result.drafted}`;
    if(e.tool==='construction_definitions')return `Building search${suffix||' · all buildings'} · ${e.result?.total??'?'} matches${e.result?.items?.length?` · ${e.result.items.slice(0,4).map((d:any)=>d.label||d.def_name).join(', ')}`:''}`;
    if(e.tool==='query')return `Inspected ${e.arguments?.endpoint?.replaceAll('_',' ')||'colony'}${suffix}${e.result?.total!=null?` · ${e.result.total} matches`:''}`;
    if(e.tool==='describe')return `Read command: ${e.arguments?.endpoint||''}`;
    if(e.tool==='discover')return `Looked up: ${e.arguments?.search||'available commands'}`;
    if(e.tool==='submit')return 'Submitted review';
    return `${e.tool}${suffix}`;
  }
  return e.text||e.error||'Response needs correction';
}
function EventRow({event:e,technical=false}:{event:any;technical?:boolean}){
 const labels:Record<string,string>={execution:'Orders',work_outcome:'Verified',arbitration:'Decision',spatial_plan:'Layout planned',project_cancelled:'Project cancelled',error:'Blocked',escalation:'Needs a decision'};
 const statuses:Record<string,string>={complete:'Verified',issued:'Sent; awaiting verification',unknown:'Outcome unknown',rejected:'Rejected',deferred:'Not sent'};
 return <article className={e.kind}><header><strong>{source(e)}</strong><span>{labels[e.kind]||e.kind.replaceAll('_',' ')}</span><time>{new Date(e.at*1000).toLocaleTimeString()}</time></header>
 {e.results?.length?<ul>{e.results.map((r:any,i:number)=><li key={r.id||i}>{r.title} — {statuses[r.status]||r.status}</li>)}</ul>:<p>{caption(e)}</p>}
 {e.blockers?.map((b:string,i:number)=><p key={i}>Blocked: {b}</p>)}
 {e.explanation&&e.explanation!==e.text&&<details><summary>Full explanation</summary><p>{e.explanation}</p></details>}
 {technical&&<details><summary>Technical details{e.tool?` · ${e.tool}`:''}</summary><pre>{JSON.stringify(e,null,2)}</pre></details>}
 </article>;
}
export default function ManagerActivity({colony,busy,status}:{colony?:string;busy?:boolean;status?:any;assignments?:Record<string,string>}){
 const [events,setEvents]=useState<any[]>([]),[role,setRole]=useState('All'),[error,setError]=useState(''),[expanded,setExpanded]=useState(false);
 useEffect(()=>{
  setEvents([]);setError('');setRole('All');setExpanded(false);
  if(!colony)return;
  const abort=new AbortController();let pending=false;
  async function refresh(){
   if(pending)return;pending=true;
   try{const r=await fetch('/api/manager-activity',{signal:abort.signal});if(!r.ok)throw Error(`Activity unavailable (${r.status})`);const data=await r.json();if(!abort.signal.aborted&&data.colony===colony){setEvents(data.events);setError('');}}
   catch(e){if(!abort.signal.aborted)setError(String(e));}finally{pending=false;}
  }
  refresh();const timer=setInterval(refresh,2000);return()=>{abort.abort();clearInterval(timer);};
 },[colony]);
 const roles=Array.from(new Set([...events.map(source),...(busy?[source(status||{})]:[])])).sort();
 const selected=events.filter(e=>role==='All'||source(e)===role),outcomes=selected.filter(outcome);
 const active=busy&&(role==='All'||source(status||{})===role);
 return <section className="mgr-card mgr-manager-log" aria-label="Colony activity">
  <div className="mgr-card-title"><h2>Colony activity</h2><a href="/api/history" download>Export full log</a></div>
  <div className="mgr-activity-controls"><label>Source <select aria-label="Activity source" value={role} onChange={e=>setRole(e.target.value)}><option>All</option>{roles.map(r=><option key={r}>{r}</option>)}</select></label>{active&&<span role="status">{source(status||{})} · {status?.phase||'Working'}</span>}</div>
  {error&&<p role="alert" className="mgr-log-status">{error}</p>}
  <div className="mgr-manager-events">{!outcomes.length&&<p className="mgr-empty">{active?'Working — no new outcome yet.':'No outcomes recorded yet.'}</p>}{outcomes.map(e=><EventRow key={e.id} event={e}/>)}</div>
  <details className="mgr-activity-details" open={expanded} onToggle={e=>setExpanded(e.currentTarget.open)}><summary>{tally(selected)} <span className="mgr-muted">· recent activity · show details</span></summary>
  {expanded&&<><p className="mgr-small">Includes inspections, proposed orders and corrections. A tool call is not a completed game action.</p><div className="mgr-manager-events">{selected.filter(e=>e.kind!=='model_call').map(e=><EventRow key={e.id} event={e} technical/>)}</div></>}
  </details>
 </section>;
}
