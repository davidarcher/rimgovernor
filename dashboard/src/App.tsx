import {useCallback,useEffect,useState} from 'react';
import BridgeColony from './features/manager/BridgeColony';
import RimWorldDashboard from './features/dashboard/Dashboard';
import Manager from './features/manager/Manager';
import {BasePlanPage} from './features/manager/SpatialPlan';
import {useController} from './features/manager/useController';
import {ToastProvider} from './components/feedback/ToastContext';
import {ToastContainer} from './components/feedback/ToastContainer';
import {ImageCacheProvider} from './components/context/ImageCacheContext';
import {AutoRefreshProvider} from './components/context/AutoRefreshContext';
import {sseService} from './services/sseService';
import {setApiBaseUrl} from './services/rimworldApi';
import './App.css';
function ColonyDetails(props:React.ComponentProps<typeof RimWorldDashboard>){
  const {state,error}=useController();
  if(!state?.connected)return <main className="mgr-shell"><h2>{state?.status.phase==='Disconnected'?'Waiting for RimWorld':'Waiting for colony'}</h2><p>{error || state?.status.detail || 'Checking the game connection…'}</p><p>Start RimWorld with RIMAPI and load a colony. This view will connect automatically.</p></main>;
  return <RimWorldDashboard {...props}/>;
}
function LegacyApp(){
  const [inspect,setInspect]=useState(false);
  const [view,setView]=useState(location.hash.slice(1));
  const basePlan=view==='base-plan';
  useEffect(()=>{const changed=()=>{setView(location.hash.slice(1));setInspect(false);};window.addEventListener('hashchange',changed);return()=>window.removeEventListener('hashchange',changed);},[]);
  const base=location.origin+'/rimapi/api/v1';
  useEffect(()=>{setApiBaseUrl(base);sseService.setApiUrl(base);if(inspect)sseService.connect();return()=>sseService.disconnect();},[base,inspect]);
  const gameChanged=useCallback(()=>{},[]);
  return <ImageCacheProvider><ToastProvider><AutoRefreshProvider>
    <div hidden={basePlan||inspect}><Manager view={view==='work'||view==='activity'?view:'colony'} onInspect={()=>setInspect(true)}/></div>
    {basePlan?<BasePlanPage/>:inspect?<><nav className="mgr-inspect-nav"><button onClick={()=>setInspect(false)}>← Colony manager</button><span>RIMAPI Dashboard · Colony details</span></nav><ColonyDetails apiUrl={base} onResetConfig={()=>setInspect(false)} onGameStateChange={gameChanged}/></>:null}
    <ToastContainer/>
  </AutoRefreshProvider></ToastProvider></ImageCacheProvider>;
}

export default function App(){
 const [backend,setBackend]=useState(''),[error,setError]=useState('');
 useEffect(()=>{fetch('/api/health').then(r=>{if(!r.ok)throw Error('Controller unavailable');return r.json();}).then(v=>setBackend(v.backend||'rimapi')).catch(e=>setError(String(e)));},[]);
 if(error)return <main className="mgr-shell"><p role="alert">{error}</p><button onClick={()=>location.reload()}>Reconnect</button></main>;
 if(!backend)return <main className="mgr-shell"><p>Connecting to colony…</p></main>;
 return backend==='rimbridge'?<BridgeColony/>:<LegacyApp/>;
}
