import BridgeColony from './features/manager/BridgeColony';
import LocalColonies from './features/manager/LocalColonies';
import ScenarioWatch from './features/manager/ScenarioWatch';
import ObservationDashboard from './features/manager/ObservationDashboard';
import {useEffect, useState} from 'react';

export default function App(){
  const [backend,setBackend]=useState<'go'|'colony'|null>(null);
  const [error,setError]=useState('');
  useEffect(()=>{
    let stopped=false,timer:ReturnType<typeof setTimeout>|undefined;
    const controller=new AbortController();
    const detect=async()=>{
      try{
        const response=await fetch('/api/health',{signal:AbortSignal.any([controller.signal,AbortSignal.timeout(5000)])});
        if(!response.ok)throw Error(`Connection unavailable (${response.status})`);
        const health:unknown=await response.json();
        if(typeof health!=='object'||health===null||!('service' in health)||health.service!=='rimgovernor')throw Error('Unrecognized colony service');
        const name='backend' in health?health.backend:undefined;
        if(name!==undefined&&name!=='rimbridge'&&name!=='go')throw Error('Unrecognized colony service');
        if(!stopped)setBackend(name==='go'?'go':'colony');
      }catch(reason){if(!stopped){setError(reason instanceof Error?reason.message:'Connection unavailable');timer=setTimeout(detect,2000);}}
    };
    void detect();return()=>{stopped=true;controller.abort();if(timer)clearTimeout(timer);};
  },[]);
  if(backend==='go')return <ObservationDashboard/>;
  if(backend==='colony')return location.pathname === '/colonies'
    ? <main className="colony-directory"><LocalColonies/></main>
    : location.pathname === '/scenario' ? <ScenarioWatch/> : <BridgeColony/>;
  return <main className="observation-shell"><header className="observation-header"><h1>RimGovernor</h1></header><p role="status">{error?`${error}. Reconnecting…`:'Connecting to your colony…'}</p></main>;
}
