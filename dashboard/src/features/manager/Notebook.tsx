import {useState} from 'react';

export type Memory={id:string;text:string;evidence:string;tick:number|null;previous_load:boolean;version:string};
export default function Notebook({notes,sessionId,onChange}:{notes:Memory[];sessionId:string;onChange:()=>Promise<unknown>}){
 const [pending,setPending]=useState<string|null>(null),[error,setError]=useState('');
 async function forget(note:Memory){
  setPending(note.id);setError('');
  try{
   const r=await fetch(`/api/memories/${encodeURIComponent(note.id)}`,{method:'DELETE',headers:{'X-RimGovernor':'1','Content-Type':'application/json'},body:JSON.stringify({session_id:sessionId,version:note.version})});
   if(!r.ok)throw Error((await r.json()).detail||'Could not forget note');
   await onChange();
  }catch(e){setError(String(e));}finally{setPending(null);}
 }
 return <section className="mgr-card bridge-detail"><p className="mgr-eyebrow">COLONY KNOWLEDGE</p><h1>Notebook</h1>
  <p className="mgr-muted">What the strategist has learned. These are past observations, not current facts or orders. Forget a mistaken note, or use chat to explain a correction.</p>
  {error&&<p role="alert">{error}</p>}
  {!notes.length&&<p>No notes yet. Useful lessons will appear here as the strategist records them.</p>}
  {notes.map(note=><article key={note.id} className="mgr-card" style={{padding:'1rem',marginTop:'1rem'}}>
   <div className="mgr-card-title"><h2>{note.id.replaceAll('-',' ')}</h2><button disabled={pending!==null} onClick={()=>forget(note)} aria-label={`Forget ${note.id.replaceAll('-',' ')}`}>{pending===note.id?'Forgetting…':'Forget'}</button></div>
   <p style={{whiteSpace:'pre-wrap'}}>{note.text}</p><small className="mgr-muted">{note.previous_load?'From a previous save load · recheck before relying on it':'Recorded during this load'}{note.tick!=null?` · tick ${note.tick}`:''}</small>
   <details><summary>Evidence</summary><p style={{whiteSpace:'pre-wrap'}}>{note.evidence}</p></details>
  </article>)}
 </section>;
}
