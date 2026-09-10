import {useState} from 'react';

export default function SessionCheckpoint({sessionId,connected}:{sessionId:string;connected:boolean}) {
 const [busy,setBusy]=useState(false),[message,setMessage]=useState(''),[error,setError]=useState('');
 async function save(){
  setBusy(true);setError('');setMessage('');
  try {
   const response=await fetch('/api/session/checkpoint',{method:'POST',headers:{'Content-Type':'application/json','X-RimGovernor':'1'},body:JSON.stringify({session_id:sessionId})});
   const result=await response.json();
   if(!response.ok)throw Error(result.detail||'Checkpoint could not be saved.');
   setMessage(`Colony and controller saved at game tick ${result.tick}. Autopilot is now in Manual.`);
  }catch(e){setError(String(e).replace(/^Error: /,''));}finally{setBusy(false);}
 }
 return <section className="mgr-card autopilot-section"><h2>Session checkpoint</h2><p>Pause the colony and save its game progress, goals, settings and chat together for a later restart.</p><button disabled={!connected||busy} onClick={save}>{busy?'Saving checkpoint…':'Save checkpoint and pause'}</button>{message&&<p role="status">{message}</p>}{error&&<p role="alert">{error}</p>}</section>;
}
