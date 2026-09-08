export type Committed = {revision:number;rationale:string;goals:string[];constraints:string[];risks:string[];steps:{id:string;title:string;priority:number;action:string;completion:string;state:string;issued:number;failure?:{detail:string}|null}[]};
export default function CommittedPlan({plan,onCancel}:{plan?:Committed;onCancel:(id:string)=>void}) {
 if(!plan?.revision)return <p className="mgr-muted">No committed plan yet. The strategist can plan in Manual; Hands executes in Automate.</p>;
 return <section><h2>Committed plan <small className="mgr-muted">revision {plan.revision}</small></h2><p>{plan.rationale}</p>
 {plan.goals.length>0&&<ul>{plan.goals.map(g=><li key={g}>{g}</li>)}</ul>}
 <div>{plan.steps.map(s=><article key={s.id} className="mgr-card" style={{padding:'12px',marginBottom:'8px'}}><div className="mgr-inline"><b>{s.title}</b><span className="mgr-pill">{s.state}</span>{s.state!=='cancelled'&&<button onClick={()=>onCancel(s.id)}>Cancel</button>}</div><p>{s.failure?.detail||s.completion}</p><small className="mgr-muted">{s.issued} operations recorded · priority {s.priority}</small></article>)}</div>
 {(plan.constraints.length>0||plan.risks.length>0)&&<details><summary>Constraints and risks</summary>{[...plan.constraints,...plan.risks].map((r,i)=><p key={i}>{r}</p>)}</details>}</section>;
}
