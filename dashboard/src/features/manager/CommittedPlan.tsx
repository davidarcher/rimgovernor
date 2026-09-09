export type Committed = {revision:number;rationale:string;goals:string[];constraints:string[];risks:string[];
 controller?:{status?:string;criteria?:Record<string,boolean>};
 colonyGoals?:Record<string,{status:string;priority_class:number;source:string;method:string;reason:string;cancelled:boolean}>;
 steps:{id:string;title:string;priority:number;action:string;completion:string;state:string;issued:number;source?:string;failure?:{detail:string}|null}[]};
export default function CommittedPlan({plan,onCancel}:{plan?:Committed;onCancel:(id:string)=>void}) {
 if(!plan)return <p className="mgr-muted">Autopilot manages routine needs. Use chat to give orders, change goals, or ask why work is blocked.</p>;
 return <section><h2>Committed plan <small className="mgr-muted">revision {plan.revision}</small></h2><p>{plan.rationale}</p>
 {plan.controller?.status&&<p role="status">{plan.controller.status==='FOOTHOLD_STABLE'?'Starter colony stable':'Establishing a starter colony'}</p>}
 {plan.controller?.criteria&&<details open><summary>Foothold checks</summary><ul>{Object.entries(plan.controller.criteria).map(([name,ok])=><li key={name}>{ok?'✓':'○'} {name}: {ok?'verified':'not yet verified'}</li>)}</ul></details>}
 {plan.colonyGoals&&Object.entries(plan.colonyGoals).map(([id,g])=><article key={id} className="mgr-card" style={{padding:'12px',marginBottom:'8px'}}><b>{id.replace(/([a-z])([A-Z])/g,'$1 $2')}</b> <span className="mgr-pill">{g.cancelled?'cancelled':g.status}</span><p>{g.reason||g.method||'Waiting for a method'}</p><small className="mgr-muted">{g.source==='PLAYER'?'Player request':'Autopilot'} · priority class {g.priority_class}</small></article>)}
 {plan.goals.length>0&&<ul>{plan.goals.map(g=><li key={g}>{g}</li>)}</ul>}
 <div>{plan.steps.map(s=><article key={s.id} className="mgr-card" style={{padding:'12px',marginBottom:'8px'}}><div className="mgr-inline"><b>{s.title}</b><span className="mgr-pill">{s.state}</span>{s.state!=='cancelled'&&<button onClick={()=>onCancel(s.id)}>Cancel</button>}</div><p>{s.failure?.detail||s.completion}</p><small className="mgr-muted">{s.issued} operations recorded · priority {s.priority}</small></article>)}</div>
 {(plan.constraints.length>0||plan.risks.length>0)&&<details><summary>Constraints and risks</summary>{[...plan.constraints,...plan.risks].map((r,i)=><p key={i}>{r}</p>)}</details>}</section>;
}
