import {useEffect,useState} from 'react';
import PawnImage from './PawnImage';
import './People.css';

export type Pawn={thing_id:string;name:string;position:{x:number;z:number};job:string|null;drafted:boolean;downed:boolean;dead:boolean;mood:number|null;food:number|null;rest:number|null;armed:boolean|null;primary_weapon:string|null;needs_tend:boolean|null;bleeding:boolean|null};
export type PeopleObservation={pawns:Pawn[];start_tick:number;end_tick:number;same_tick:boolean};
type Gear={label:string;conditionPct?:number|null};
type Thought={label:string;moodOffset:number|null;count:number};
export type NativePawn={thingId:string;name:string;position:Pawn['position'];job:string|null;drafted:boolean;downed:boolean;dead:boolean;mentalState?:string|null;jobReport?:string|null;
 needs?:{mood:number|null;food:number|null;rest:number|null}|null;
 health?:{needsTend:boolean|null;bleeding:boolean|null;hediffs?:{label:string;part?:string;isTended?:boolean}[]}|null;
 equipment?:{armed:boolean;primaryLabel:string;apparel:Gear[];equipped:Gear[];inventoryWeapons:Gear[]};
 bio?:{ageBiological?:number;childhood?:string;adulthood?:string;traits?:{label:string;description:string;suppressed:boolean}[];skills?:{label:string;level:number|null;passion:string;disabled?:boolean}[];incapableOf?:string[];incapableOfRead?:boolean};
 thoughts?:{hasThoughtHandler:boolean;memories:Thought[];situational:Thought[];situationalCacheStale?:boolean;situationalCacheReadable?:boolean};
};
type Readings={sessionId:string;pawns:NativePawn[];startTick:number;endTick:number;observedAt:number};
const percent=(value:number|null|undefined)=>value==null?'Unknown':`${Math.round(value*100)}%`;
const condition=(p:Pawn)=>[p.dead&&'Deceased',p.downed&&'Downed',p.bleeding&&'Bleeding',p.needs_tend&&'Needs tending',p.drafted&&'Drafted'].filter(Boolean).join(' · ');
const summary=(p:NativePawn):Pawn=>({thing_id:p.thingId,name:p.name,position:p.position,job:p.jobReport||p.job,drafted:p.drafted,downed:p.downed,dead:p.dead,mood:p.needs?.mood??null,food:p.needs?.food??null,rest:p.needs?.rest??null,armed:p.equipment?.armed??null,primary_weapon:p.equipment?.primaryLabel??null,needs_tend:p.health?.needsTend??null,bleeding:p.health?.bleeding??null});
const weapon=(p:Pawn)=>p.armed==null?'Weapon unknown':p.armed?p.primary_weapon||'Equipped (unspecified)':'Unarmed';

export default function People({observation,stale,sessionId='',active=true,headless=false}:{observation?:PeopleObservation|null;stale:boolean;sessionId?:string;active?:boolean;headless?:boolean}){
 const [selected,setSelected]=useState<string|null>(null),[readings,setReadings]=useState<Readings|null>(null),[error,setError]=useState(''),[visible,setVisible]=useState(!document.hidden),[following,setFollowing]=useState(false),[followOpened,setFollowOpened]=useState(false);
 const [history,setHistory]=useState<{id:string;job:string;tick:number}[]>([]);
 useEffect(()=>{const change=()=>setVisible(!document.hidden);document.addEventListener('visibilitychange',change);return()=>document.removeEventListener('visibilitychange',change);},[]);
 useEffect(()=>{setSelected(null);setReadings(null);setHistory([]);setFollowing(false);setFollowOpened(false);},[sessionId]);
 const enabled=active&&visible&&!stale;
 useEffect(()=>{
  if(!enabled||!sessionId)return;
  let stopped=false,timer:ReturnType<typeof setTimeout>;
  const controller=new AbortController();
  const poll=async()=>{
   try{
    const result=await fetch(`/api/people?${new URLSearchParams({session_id:sessionId})}`,{signal:controller.signal});
    if(!result.ok)throw Error('Colonist details could not refresh. Last readings retained.');
    const data:Readings=await result.json();
    if(data.sessionId!==sessionId||!Array.isArray(data.pawns))throw Error('Waiting for current colony details.');
    if(!stopped){setReadings(data);setError('');}
   }catch(e){if(!stopped)setError(e instanceof Error?e.message:'Details unavailable');}
   if(!stopped)timer=setTimeout(poll,2500);
  };
  void poll();
  return()=>{stopped=true;controller.abort();clearTimeout(timer);};
 },[enabled,sessionId]);
 const current=readings?.sessionId===sessionId?readings:null;
 const pawns=current?current.pawns.map(summary):observation?.pawns||[];
 const pawn=pawns.find(p=>p.thing_id===selected),detail=current?.pawns.find(p=>p.thingId===selected);
 const tick=current?.endTick??observation?.end_tick;
 useEffect(()=>{
  if(!pawn||tick==null)return;
  const job=pawn.dead?'Deceased':pawn.job||'No current job';
  setHistory(old=>old[0]?.id===pawn.thing_id&&old[0]?.job===job&&tick>=old[0].tick?old:
   [{id:pawn.thing_id,job,tick},...(old[0]?.id===pawn.thing_id&&tick>=old[0].tick?old:[])].slice(0,8));
 },[pawn?.thing_id,pawn?.job,pawn?.dead,tick]);
 const imageActive=enabled&&!headless;
 const thoughts=detail?.thoughts;
 return <section className="mgr-card bridge-detail">
  <div className="people-heading"><div><p className="mgr-eyebrow">THE PEOPLE WHO MAKE THIS PLACE</p><h1>Colonists</h1></div><span className="people-live">{enabled&&!error&&current?'● Updating every few seconds':'Last colony observation'}{tick!=null?` · tick ${tick}`:''}</span></div>
  <p className="mgr-muted">Meet the colony. Open a card to see their story, gear, mood and what they are doing.</p>
  {stale&&<p role="status">Connection interrupted. These are the last observations, not live readings.</p>}
  {error&&<p role="status">{error}</p>}
  {current&&current.startTick!==current.endTick?<small className="mgr-muted">Game advanced during collection ({current.startTick}–{current.endTick}).</small>:!current&&observation&&!observation.same_tick?<small className="mgr-muted">Game advanced during collection ({observation.start_tick}–{observation.end_tick}).</small>:null}
  {!observation&&!current&&<p>Waiting for colony observations…</p>}
  {(observation||current)&&!pawns.length&&<p>No colonists in the current observation.</p>}
  <div className="people-layout"><div className="people-roster">{pawns.map(p=><button className="person-card" key={p.thing_id} aria-label={p.name} aria-pressed={p.thing_id===selected} onClick={()=>{setSelected(p.thing_id);setFollowing(false);setFollowOpened(false);}}>
   <PawnImage id={p.thing_id} name={p.name} sessionId={sessionId} active={imageActive}/>
   <span><strong>{p.name}</strong><small className="person-job">{p.dead?'Deceased':p.job||'No current job'}</small><small>{weapon(p)}</small>{condition(p)&&<small className="person-condition">{condition(p)}</small>}</span>
  </button>)}</div>
  <div className="person-detail">
   {!selected&&<div className="person-empty">Select a colonist to get to know them.</div>}
   {selected&&!pawn&&<p>The selected colonist is no longer in this observation.</p>}
   {pawn&&<article aria-label={`${pawn.name} details`}>
    <div className="person-summary"><PawnImage id={pawn.thing_id} name={pawn.name} sessionId={sessionId} active={imageActive}/><div><p className="mgr-eyebrow">COLONIST DOSSIER</p><h2>{pawn.name}</h2><p>{[detail?.bio?.ageBiological!=null?`Age ${detail.bio.ageBiological}`:null,detail?.bio?.adulthood||detail?.bio?.childhood].filter(Boolean).join(' · ')}</p><p>{pawn.dead?'Deceased':pawn.job||'No current job reported'}</p>{condition(pawn)&&<p className="person-condition">{condition(pawn)}</p>}{detail?.mentalState&&<p className="person-condition">{detail.mentalState}</p>}</div></div>
    <div className="person-needs">{[['Mood',pawn.mood],['Food need',pawn.food],['Rest',pawn.rest]].map(([label,value])=><div className="person-need" key={String(label)}><span>{label} <strong>{percent(value as number|null)}</strong></span>{value!=null&&<meter aria-label={String(label)} min={0} max={1} value={value as number}/>}</div>)}</div>
    <div className="person-panels">
     <section className="person-panel person-wide"><div className="people-heading"><h3>Follow {pawn.name}</h3><button disabled={headless||stale||!sessionId} aria-pressed={following} onClick={()=>{setFollowOpened(true);setFollowing(value=>!value);}}>{following?'Pause follow view':'Start follow view'}</button></div><p className="mgr-muted">A separate view of their surroundings, refreshed about once a second. The main view and game speed stay as they are.</p>{headless?<p>A rendered game is needed for images.</p>:followOpened&&<PawnImage key={pawn.thing_id} id={pawn.thing_id} name={pawn.name} sessionId={sessionId} active={imageActive&&following} view="follow"/>}</section>
     <section className="person-panel"><h3>Equipment & clothing</h3><p><strong>{weapon(pawn)}</strong></p>{detail?.equipment?<><GearList gear={detail.equipment.apparel} empty="No worn apparel reported."/>{!!detail.equipment.inventoryWeapons?.length&&<><p>Weapons in inventory</p><GearList gear={detail.equipment.inventoryWeapons} empty=""/></>}</>:<p>Clothing details not yet available.</p>}</section>
     <section className="person-panel"><h3>Health</h3><div className="person-pair"><span>Tending needed</span><span>{pawn.needs_tend==null?'Unknown':pawn.needs_tend?'Yes':'No'}</span></div><div className="person-pair"><span>Bleeding</span><span>{pawn.bleeding==null?'Unknown':pawn.bleeding?'Yes':'No'}</span></div>{detail?.health?.hediffs?<ul>{detail.health.hediffs.length?detail.health.hediffs.map((h,i)=><li key={i}>{h.label}{h.part?` · ${h.part}`:''}{h.isTended?' · tended':''}</li>):<li>No visible conditions reported.</li>}</ul>:<p>Detailed health readings not yet available.</p>}</section>
     <section className="person-panel person-wide"><h3>On their mind</h3>{!thoughts?<p>Waiting for native thoughts…</p>:!thoughts.hasThoughtHandler?<p>Thoughts are unavailable for this pawn.</p>:<>{(thoughts.situationalCacheStale||thoughts.situationalCacheReadable===false)&&<p className="mgr-muted">Situational thoughts are {thoughts.situationalCacheReadable===false?'unavailable':'waiting for the game to refresh its cache'}.</p>}<ThoughtList title="Memories" rows={thoughts.memories}/><ThoughtList title="Current circumstances" rows={thoughts.situational}/></>}</section>
     <section className="person-panel"><h3>Biography & traits</h3>{detail?.bio?<><p>{detail.bio.childhood||'Childhood unavailable'}{detail.bio.adulthood?` → ${detail.bio.adulthood}`:''}</p><div className="person-tags">{detail.bio.traits?.map((t,i)=><span key={i} title={t.description}>{t.label}{t.suppressed?' (suppressed)':''}</span>)}</div>{detail.bio.incapableOfRead===false?<p>Work limitations unavailable.</p>:!!detail.bio.incapableOf?.length&&<p>Cannot do: {detail.bio.incapableOf.join(', ')}</p>}</>:<p>Biography not yet available.</p>}</section>
     <section className="person-panel"><h3>Skills</h3>{detail?.bio?.skills?.length?detail.bio.skills.map((s,i)=><div className="person-pair" key={i}><span>{s.label}{s.passion==='Major'?' · ★★':s.passion==='Minor'?' · ★':''}</span><strong>{s.disabled?'Disabled':s.level??'Unknown'}</strong></div>):<p>Skills not yet available.</p>}</section>
     <section className="person-panel person-wide"><h3>Recent observed actions</h3><p className="mgr-muted">Job changes seen while this dossier is open; short jobs between readings may be missed.</p><ol aria-label="Recent observed actions">{history.filter(h=>h.id===pawn.thing_id).map((h,i)=><li key={i}>{h.job} <small className="mgr-muted">· tick {h.tick}</small></li>)}</ol></section>
    </div>
    <details className="technical"><summary>Native observation details</summary><p>Native ID: {pawn.thing_id} · Position: {pawn.position.x}, {pawn.position.z}. No current job alone does not prove a pawn is idle or available for work.</p></details>
   </article>}
  </div></div>
 </section>;
}
function GearList({gear,empty}:{gear?:Gear[];empty:string}){return gear?.length?<ul>{gear.map((g,i)=><li key={i}>{g.label}{g.conditionPct!=null?` · ${percent(g.conditionPct)} condition`:''}</li>)}</ul>:<p>{empty}</p>;}
function ThoughtList({title,rows}:{title:string;rows?:Thought[]}){return <><p><strong>{title}</strong></p>{rows?.length?rows.map((t,i)=><div className="person-pair" key={i}><span>{t.label}{t.count>1?` ×${t.count}`:''}</span><strong className={(t.moodOffset??0)<0?'person-thought-negative':'person-thought-positive'}>{t.moodOffset==null?'Unknown':`${t.moodOffset>0?'+':''}${t.moodOffset}`}</strong></div>):<p>No {title.toLowerCase()} reported.</p>}</>;}
