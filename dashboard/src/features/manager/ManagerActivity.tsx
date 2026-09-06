import {useEffect,useState} from 'react';

const roles=['All','Strategy','Infrastructure','Survival','Security','Development','Workforce','Administrator','Daily planning'];
const owner=(e:any)=>(e.role||'Colony').split(':')[0];
function caption(e:any){
  if(e.kind==='model_call')return `Review finished · ${Number(e.seconds||0).toFixed(1)}s · ${e.tools?.length||0} tool calls`;
  if(e.kind==='tool_result'){
    if(e.result?.error)return `Needs correction: ${e.result.error}`;
    if(e.result?.drafted)return `Drafted: ${e.result.drafted}`;
    if(e.tool==='query')return `Inspected ${e.arguments?.endpoint?.replaceAll('_',' ')||'colony'}${e.result?.total!=null?` · ${e.result.total} matches`:''}`;
    if(e.tool==='describe')return `Read command: ${e.arguments?.endpoint||''}`;
    if(e.tool==='discover')return `Looked up: ${e.arguments?.search||'available commands'}`;
    if(e.tool==='submit')return 'Submitted review';
    return e.tool;
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
    <div className="mgr-log-status"><strong>{active?`${status?.role||'Manager'} · ${status?.phase||'Reviewing'}`:role==='All'?'Recent colony reviews':role}</strong><span>{active?status?.detail:assignments?.[role]||'Inspections → proposals → arbitration → orders'}</span>{active&&status?.model&&<small>Model: {status.model}</small>}<small>{visible.filter(e=>e.kind==='tool_result').length} tools · {visible.filter(e=>e.kind==='proposal').length} proposals · {visible.filter(e=>e.kind==='arbitration').length} decisions in recent history</small></div>
    {error&&<p role="alert" className="mgr-log-status">{error}</p>}
    <div className="mgr-manager-events">{!visible.length&&<p className="mgr-empty">No recorded activity{role==='All'?'':` for ${role}`} yet.</p>}{visible.map(e=><article key={e.id} className={e.kind}>
      <header><strong>{owner(e)}</strong><span>{e.kind==='tool_result'?'Tool':e.kind.replaceAll('_',' ')}</span><time>{new Date(e.at*1000).toLocaleTimeString()}</time></header>
      <p>{caption(e)}</p>
      {e.kind==='proposal'&&<small>{e.proposal?.actions?.length||0} proposed orders · awaiting arbitration</small>}
      {e.kind==='arbitration'&&<div className="mgr-verdict">{e.accepted?.map((r:string)=><span key={r}>Approved: {r}</span>)}{Object.entries(e.deferred||{}).map(([r,why])=><p key={r}>Deferred {r}: {String(why)}</p>)}<small>Approval is not confirmation that an order executed. See Work for results.</small></div>}
      {e.proposal?.actions?.map((a:any,i:number)=><div key={i}>↳ {a.title}</div>)}
      <details><summary>Details{e.tool?` · ${e.tool}`:''}</summary><pre>{JSON.stringify(e,null,2)}</pre></details>
    </article>)}</div><p className="mgr-small mgr-log-status">Latest 300 events, newest first. Drafts are proposed orders; Work tracks what reached the game.</p>
  </section>;
}
