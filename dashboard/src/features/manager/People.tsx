import {useState} from 'react';

export type Pawn={thing_id:string;name:string;position:{x:number;z:number};job:string|null;drafted:boolean;downed:boolean;dead:boolean;mood:number|null;food:number|null;rest:number|null;armed:boolean|null;primary_weapon:string|null;needs_tend:boolean|null;bleeding:boolean|null};
export type PeopleObservation={pawns:Pawn[];start_tick:number;end_tick:number;same_tick:boolean};
const percent=(value:number|null)=>value==null?'Unknown':`${Math.round(value*100)}%`;
export default function People({observation,stale}:{observation?:PeopleObservation|null;stale:boolean}){
 const [selected,setSelected]=useState<string|null>(null);
 const pawns=observation?.pawns||[],pawn=pawns.find(p=>p.thing_id===selected);
 return <section className="mgr-card bridge-detail"><p className="mgr-eyebrow">NATIVE OBSERVATIONS</p><h1>People</h1>
  <p className="mgr-muted">Current work, equipment and needs. Select a colonist for details.</p>
  {stale&&<p role="status">Connection interrupted. These are the last observations, not live readings.</p>}
  {observation?<small className="mgr-muted">Observed at tick {observation.end_tick}{!observation.same_tick?` · game advanced during collection (${observation.start_tick}–${observation.end_tick})`:''}</small>:<p>Waiting for colony observations…</p>}
  {observation&&!pawns.length&&<p>No colonists in the current observation.</p>}
  <div style={{overflowX:'auto'}}><table style={{width:'100%',textAlign:'left',borderSpacing:'0 12px'}}><thead><tr><th>Colonist</th><th>Current job</th><th>Weapon</th><th>Attention</th></tr></thead><tbody>{pawns.map(p=><tr key={p.thing_id}>
   <td><button aria-pressed={p.thing_id===selected} onClick={()=>setSelected(p.thing_id)}>{p.name}</button></td><td>{p.dead?'Deceased':p.job||'No current job'}</td><td>{p.armed==null?'Unknown':p.armed?p.primary_weapon||'Equipped (unspecified)':'Unarmed'}</td><td>{[p.dead&&'Deceased',p.downed&&'Downed',p.bleeding&&'Bleeding',p.needs_tend&&'Needs tending',p.drafted&&'Drafted'].filter(Boolean).join(' · ')||'No flagged condition'}</td>
  </tr>)}</tbody></table></div>
  {selected&&!pawn&&<p>The selected colonist is no longer in this observation.</p>}
  {pawn&&<article className="mgr-card" style={{padding:'1rem'}}><h2>{pawn.name}</h2><dl>
   <dt>Mood</dt><dd>{percent(pawn.mood)}</dd><dt>Food need</dt><dd>{percent(pawn.food)}</dd><dt>Rest</dt><dd>{percent(pawn.rest)}</dd>
   <dt>Tending needed</dt><dd>{pawn.needs_tend==null?'Unknown':pawn.needs_tend?'Yes':'No'}</dd><dt>Bleeding</dt><dd>{pawn.bleeding==null?'Unknown':pawn.bleeding?'Yes':'No'}</dd>
   <dt>Position</dt><dd>{pawn.position.x}, {pawn.position.z}</dd></dl><details className="technical"><summary>Native diagnostic identity</summary><small className="mgr-muted">Native ID: {pawn.thing_id}. No current job alone does not prove a pawn is idle or available for work.</small></details></article>}
 </section>;
}
