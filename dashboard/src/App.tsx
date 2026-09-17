import ObservationDashboard from './features/manager/ObservationDashboard';
import PlayerGuide from './features/manager/PlayerGuide';
import {useEffect, useState} from 'react';

export default function App(){
  const [connected,setConnected]=useState(false);
  const [error,setError]=useState('');
  const [hash,setHash]=useState(location.hash);
  useEffect(()=>{
    const onHashChange=()=>setHash(location.hash);
    window.addEventListener('hashchange',onHashChange);
    return()=>window.removeEventListener('hashchange',onHashChange);
  },[]);
  useEffect(()=>{
    let stopped=false,timer:ReturnType<typeof setTimeout>|undefined;
    const controller=new AbortController();
    const detect=async()=>{
      try{
        const response=await fetch('/api/health',{signal:AbortSignal.any([controller.signal,AbortSignal.timeout(5000)])});
        if(!response.ok)throw Error(`Connection unavailable (${response.status})`);
        const health:unknown=await response.json();
        if(typeof health!=='object'||health===null||!('service' in health)||health.service!=='rimgovernor')throw Error('Unrecognized colony service');
        if(!stopped){setConnected(true);setError('');}
      }catch(reason){if(!stopped){setConnected(false);setError(reason instanceof Error?reason.message:'Connection unavailable');}}
      if(!stopped)timer=setTimeout(() => void detect(), 2000);
    };
    void detect();return()=>{stopped=true;controller.abort();if(timer)clearTimeout(timer);};
  },[]);
  if(connected)return <ObservationDashboard/>;
  const helpOpen=hash.startsWith('#help');
  return <main className="observation-shell">
    <header className="observation-header"><div><p className="observation-eyebrow">Colony field station</p><h1>RimGovernor</h1></div></header>
    <nav className="observation-nav" aria-label="Dashboard sections"><a href="#help" aria-current={helpOpen?'page':undefined}>Help</a></nav>
    {helpOpen?<PlayerGuide/>:<>
      <p role="status" className={error?'observation-notice observation-warning':'observation-notice'}>{error?`${error}. Reconnecting…`:'Waiting for the game controller…'}</p>
      <section className="observation-panel"><h2>Start the game</h2>
        <p>This dashboard connects once the RimGovernor controller is running and a colony is loaded.</p>
        <p>On Windows, run <code>launch.cmd</code> from the repository root, then load or start a save in RimWorld.</p>
        <p>See <a href="#help">Help</a> for full setup and launch instructions.</p>
      </section>
    </>}
  </main>;
}
