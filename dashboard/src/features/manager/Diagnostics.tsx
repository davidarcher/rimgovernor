import {useEffect,useState} from 'react';

export default function Diagnostics(){
  const [events,setEvents]=useState<any[]>([]),[error,setError]=useState(''),[loaded,setLoaded]=useState(false);
  useEffect(()=>{
    const abort=new AbortController();let pending=false;
    async function refresh(){
      if(pending)return;pending=true;
      try{const r=await fetch('/api/diagnostics',{signal:abort.signal});if(!r.ok)throw Error(`Diagnostics unavailable (${r.status})`);const data=await r.json();if(!abort.signal.aborted){setEvents(data);setError('');setLoaded(true);}}
      catch(e){if(!abort.signal.aborted)setError(String(e));}finally{pending=false;}
    }
    refresh();const interval=setInterval(refresh,5000);
    return()=>{abort.abort();clearInterval(interval);};
  },[]);
  return <section className="mgr-diagnostics" aria-label="Manager diagnostics"><h3>Diagnostics</h3><p className="mgr-muted">Latest 100 validation failures, newest first. A failure may have been corrected on a later attempt.</p>
    {error&&<p role="alert">{error}</p>}
    {!events.length&&<p>{loaded?'No saved validation failures for this colony.':'Loading diagnostics…'}</p>}
    {events.map(e=><article key={e.id}><header><strong>{e.role}</strong><span>{e.call?.function?.name||`${e.expected||'Structured'} submission`}</span><time>{new Date(e.at*1000).toLocaleTimeString()}</time></header>
      {e.error?<p>{e.error}</p>:<ul>{e.errors?.map((v:any,i:number)=><li key={i}>{v.loc?.join('.')||'Response'}: {v.msg}</li>)}</ul>}
      <details><summary>{e.call?'Tool arguments':'Model response'}</summary><pre>{e.call?.function?.arguments||e.response?.content||JSON.stringify(e.response,null,2)}</pre></details>
    </article>)}
  </section>;
}
