import {useEffect,useState} from 'react';

const roles=['All','Executor','Strategy','Infrastructure','Survival','Security','Development','Workforce','Administrator','Daily planning'];
const owner=(e:any)=>(e.role||'Colony').split(':')[0];
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
export default function ManagerActivity({colony,busy,status,assignments}:{colony?:string;busy?:boolean;status?:any;assignments?:Record<string,string>}){
  const [events,setEvents]=useState<any[]>([]),[role,setRole]=useState('All'),[error,setError]=useState('');
  useEffect(()=>{
    setEvents([]);setError('');
    if(!colony)return;
    const abort=new AbortController();let pending=false;
    async function refresh(){
      if(pending)return;pending=true;
      try{const r=await fetch('/api/manager-activity',{signal:abort.signal});if(!r.ok)throw Error(`Activity unavailable (${r.status})`);const data=await r.json();if(!abort.signal.aborted&&data.colony===colony){setEvents(data.events);setError('');}}
      catch(e){if(!abort.signal.aborted)setError(String(e));}finally{pending=false;}
    }
    refresh();const timer=setInterval(refresh,2000);return()=>{abort.abort();clearInterval(timer);};
  },[colony]);
  const visible=events.filter(e=>e.kind!=='model_call'&&(role==='All'||owner(e)===role));
  const active=busy&&(role==='All'||owner(status||{})===role);
  return <section className="mgr-card mgr-manager-log" aria-label="Manager activity">
    <div className="mgr-card-title"><div><span className="mgr-eyebrow">BEHIND THE PLAN</span><h2>Manager activity</h2></div><a href="/api/history" download>Export full log</a></div>
    <div className="mgr-tabs mgr-role-tabs" aria-label="Filter manager">{roles.map(r=><button key={r} aria-pressed={r===role} onClick={()=>setRole(r)}>{r}{busy&&owner(status||{})===r?' •':''}</button>)}</div>
    <div className="mgr-log-status"><strong>{active?`${status?.role||'Manager'} · ${status?.phase||'Reviewing'}`:role==='All'?'Recent colony reviews':role}</strong><span>{active?status?.detail:assignments?.[role]||'Inspections → proposals → arbitration → orders'}</span>{busy&&status?.active_roles?.length>1&&<small>Reviewing: {status.active_roles.join(' · ')}</small>}{active&&status?.model&&<small>Model: {status.model}</small>}<small>{visible.filter(e=>e.kind==='tool_result').length} tools · {visible.filter(e=>e.kind==='proposal').length} proposals · {visible.filter(e=>e.kind==='arbitration').length} decisions in recent history</small></div>
    {error&&<p role="alert" className="mgr-log-status">{error}</p>}
    <div className="mgr-manager-events">{!visible.length&&<p className="mgr-empty">No recorded activity{role==='All'?'':` for ${role}`} yet.</p>}{visible.map(e=><article key={e.id} className={e.kind}>
      <header><strong>{owner(e)}</strong><span>{e.kind==='tool_result'?'Tool':e.kind.replaceAll('_',' ')}</span><time>{new Date(e.at*1000).toLocaleTimeString()}</time></header>
      <p>{caption(e)}</p>
      {e.kind==='proposal'&&<small>{e.semantic?e.proposal?.objectives?.length||0:e.proposal?.actions?.length||0} proposed {e.semantic?'objectives':'orders'} · awaiting arbitration</small>}
      {e.kind==='arbitration'&&<div className="mgr-verdict">{e.accepted?.map((r:string)=><span key={r}>Approved: {r}</span>)}{Object.entries(e.deferred||{}).map(([r,why])=><p key={r}>Deferred {r}: {String(why)}</p>)}<small>Approval is not confirmation that an order executed. See Work for results.</small></div>}
      {e.proposal?.objectives?.map((o:any,i:number)=><div key={'objective-'+i}>{o.outcome}</div>)}
      {e.proposal?.actions?.map((a:any,i:number)=><div key={i}>↳ {a.title}</div>)}
      <details><summary>Details{e.tool?` · ${e.tool}`:''}</summary><pre>{JSON.stringify(e,null,2)}</pre></details>
    </article>)}</div><p className="mgr-small mgr-log-status">Latest 300 events, newest first. Drafts are proposed orders; Work tracks what reached the game.</p>
  </section>;
}
