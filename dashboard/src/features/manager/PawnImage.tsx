import {useEffect, useRef, useState} from 'react';

export default function PawnImage({id, name, sessionId, active, view = 'portrait'}:
 {id:string; name:string; sessionId:string; active:boolean; view?:'portrait'|'follow'}) {
 const [frame,setFrame]=useState(''), [error,setError]=useState(''), [tick,setTick]=useState('');
 const retained=useRef('');
 useEffect(()=>{
  setFrame('');setError('');setTick('');
  return()=>{if(retained.current)URL.revokeObjectURL(retained.current);retained.current='';};
 },[id,sessionId,view]);
 useEffect(()=>{
  let stopped=false, timer:ReturnType<typeof setTimeout>;
  const controller=new AbortController();
  if(!active||!sessionId)return;
  const poll=async()=>{
   try {
    const query=new URLSearchParams({session_id:sessionId,view});
    const response=await fetch(`/api/people/${encodeURIComponent(id)}/image?${query}`,{signal:controller.signal});
    if(!response.ok)throw Error(view==='follow'?'Follow view unavailable. A rendered game with the pawn-image bridge is required.':'Portrait unavailable');
    const blob=await response.blob();
    if(stopped)return;
    const next=URL.createObjectURL(blob);
    if(retained.current)URL.revokeObjectURL(retained.current);
    retained.current=next;setFrame(next);setTick(response.headers.get('X-Observed-Tick')||'');setError('');
   }catch(e){if(!stopped)setError(e instanceof Error?e.message:'Image unavailable');}
   if(!stopped)timer=setTimeout(poll,view==='follow'?1000:15000);
  };
  void poll();
  return ()=>{stopped=true;controller.abort();clearTimeout(timer);};
 },[id,sessionId,active,view]);
 return <figure className={`pawn-image pawn-image-${view}`}>
  {frame?<img src={frame} alt={view==='portrait'?`${name} with current apparel and equipped weapon icon`:`Follow view of ${name}`}/>:<div className="pawn-image-empty" aria-label={`${name} image unavailable`}>{view==='portrait'?name.slice(0,1):'Waiting for the game view…'}</div>}
  {view==='follow'?<figcaption aria-live="polite">{error||`${active?'Following':'Paused'}${tick?` · observed at tick ${tick}`:''}`}{error&&frame?' Last image retained.':''}</figcaption>:error&&<figcaption>Portrait unavailable</figcaption>}
 </figure>;
}
