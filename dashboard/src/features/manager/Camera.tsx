import {useEffect,useRef,useState} from 'react';
import {command} from './useController';

export default function Camera({connected}:{connected:boolean}) {
  const [live,setLive]=useState(false),[image,setImage]=useState(''),[error,setError]=useState('');
  const [frames,setFrames]=useState(0),[waiting,setWaiting]=useState(false);
  const [stale,setStale]=useState(false);
  const lastFrame=useRef(0);
  useEffect(()=>{
    if(!live || !connected)return;
    let current='',count=0,disposed=false;
    setWaiting(true);setError('');lastFrame.current=Date.now();
    const ws=new WebSocket(`${location.protocol==='https:'?'wss:':'ws:'}//${location.host}/api/video`);
    ws.binaryType='blob';
    ws.onmessage=e=>{
      if(typeof e.data==='string'){try{setError(JSON.parse(e.data).error||'');}catch{}return;}
      const next=URL.createObjectURL(e.data);setImage(next);if(current)URL.revokeObjectURL(current);current=next;
      count++;lastFrame.current=Date.now();setWaiting(false);setStale(false);
    };
    ws.onerror=()=>setError('Camera connection failed. Check that RIMAPI supports camera streaming.');
    ws.onclose=()=>{if(!disposed){setLive(false);setWaiting(false);setStale(true);}};
    const interval=setInterval(()=>{setFrames(count);count=0;if(Date.now()-lastFrame.current>5000)setStale(true);},1000);
    return()=>{disposed=true;ws.close();clearInterval(interval);if(current)URL.revokeObjectURL(current);setImage('');};
  },[live,connected]);
  async function capture(){try{setError('');const result=await command('camera');setImage(result.image);setStale(false);}catch(e){setError(String(e));}}
  return <section className="mgr-card mgr-camera"><div className="mgr-card-title"><div><span className="mgr-eyebrow">ON THE GROUND</span><h2>Colony view</h2></div><div className="mgr-inline"><button disabled={!connected||live} onClick={capture}>Snapshot</button><button className={live?'mgr-selected':''} disabled={!connected} onClick={()=>setLive(v=>!v)}>{live?'Stop feed':'Live feed'}</button></div></div>
    <div className="mgr-camera-surface">{image?<img src={image} alt="RimWorld camera"/>:<div className="mgr-camera-empty"><span>⌖</span><h3>A place to call home.</h3><p>{connected?'Start the live feed to watch the colony.':'Load your colony with RIMAPI enabled.'}</p></div>}
    {live&&<div className="mgr-feed-label"><i/> {waiting?'Connecting camera…':stale?'Waiting for frames':`${frames} fps · LIVE`}</div>}
    {error&&<div className="mgr-camera-error" role="status">{error}</div>}</div>
    <div className="mgr-camera-note">{live?'Live game camera · 720p · streamed locally':'Video stays on your computer. It is not continuously sent to the model.'}</div></section>;
}
