import {useState} from 'react';

const labels:Record<string,string>={suspended:'Paused for urgent care',retired:'Retired',approved:'Approved',inspecting:'Checking details',awaiting_work:'Watching orders',orders_verified:'Orders verified; review outcome',needs_review:'Needs review'};
export default function Projects({projects,onCancel}:{projects:any[],onCancel:(id:string)=>void}){
  const [entries,setEntries]=useState<any[]>([]),[open,setOpen]=useState(false),[error,setError]=useState('');
  async function toggle(){
    if(!open&&!entries.length){try{const r=await fetch('/api/strategies');if(!r.ok)throw Error('Strategy library unavailable');setEntries((await r.json()).entries);}catch(e){setError(String(e));}}
    setOpen(!open);
  }
  return <section className="mgr-card"><div className="mgr-card-title"><h2>Projects</h2><button onClick={toggle} aria-expanded={open}>Strategy library</button></div>
    {!projects.some(p=>p.status!=='retired')&&<p className="mgr-empty">Approved objectives appear here. Orders and their progress appear below.</p>}
    {projects.filter(p=>p.status!=='retired').map(p=><article className="mgr-work-row" key={p.project_id}><div><b>{p.outcome}</b><p>{labels[p.status]||p.status}</p><small>{p.owner} · {p.kind.replaceAll('_',' ')} · {p.work_ids.length} tracked orders</small>
      {p.dependency_blockers?.map((b:string,i:number)=><p key={'dependency'+i}>{b}</p>)}
      {p.deadline_overdue&&<p>Target date passed; awaiting review.</p>}
      {p.work_allocation&&<details><summary>Work coverage</summary><p>Selected coverage; see orders below for verified changes.</p><ul>{p.work_allocation.selected.map((s:any)=><li key={s.pawn_id+':'+s.work_type}>{s.pawn_name||s.pawn_id} · {s.work_type} · priority {s.priority}</li>)}</ul></details>}
      {p.progress_note&&<p>{p.progress_note}</p>}
      {p.feedback?.map((f:string,i:number)=><p key={i}>{f}</p>)}
      <details><summary>Scope and completion criteria</summary>{p.quantity!=null&&<p>Target: {p.quantity}</p>}<ul>{p.constraints.map((c:string,i:number)=><li key={'c'+i}>{c}</li>)}{p.success_signals.map((c:string,i:number)=><li key={'s'+i}>{c}</li>)}</ul></details></div><button title="Cancel project and stop tracking its orders; existing game orders stay in place" aria-label={`Cancel ${p.outcome}`} onClick={()=>onCancel(p.project_id)}>Cancel</button></article>)}
    {open&&<div><p className="mgr-muted">Conditional guidance. Current game facts and your direction take precedence.</p>{error&&<p role="alert">{error}</p>}{entries.map(e=><details key={e.id}><summary>{e.title} · v{e.version}</summary>{[['When to use',e.applies_when],['Approach',e.approach],['Check',e.verify],['Reconsider when',e.reconsider]].map(([title,items]:any)=><div key={title}><b>{title}</b><ul>{items.map((text:string,i:number)=><li key={i}>{text}</li>)}</ul></div>)}<p className="mgr-muted">Sources: {e.sources?.map((s:any,i:number)=><span key={s.url}>{i>0&&" · "}<a href={s.url} target="_blank" rel="noreferrer">{s.title}</a> (checked {s.checked_on})</span>)}</p></details>)}</div>}
  </section>;
}
