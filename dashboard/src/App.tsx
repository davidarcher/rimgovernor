import {useCallback,useEffect,useState} from 'react';
import RimWorldDashboard from './features/dashboard/Dashboard';
import Manager from './features/manager/Manager';
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
export default function App(){
  const [inspect,setInspect]=useState(false);
  const base=location.origin+'/rimapi/api/v1';
  useEffect(()=>{setApiBaseUrl(base);sseService.setApiUrl(base);if(inspect)sseService.connect();return()=>sseService.disconnect();},[base,inspect]);
  const gameChanged=useCallback(()=>{},[]);
  return <ImageCacheProvider><ToastProvider><AutoRefreshProvider>
    {inspect?<><nav className="mgr-inspect-nav"><button onClick={()=>setInspect(false)}>← Colony manager</button><span>RIMAPI Dashboard · Colony details</span></nav><ColonyDetails apiUrl={base} onResetConfig={()=>setInspect(false)} onGameStateChange={gameChanged}/></>:<Manager onInspect={()=>setInspect(true)}/>}
    <ToastContainer/>
  </AutoRefreshProvider></ToastProvider></ImageCacheProvider>;
}
