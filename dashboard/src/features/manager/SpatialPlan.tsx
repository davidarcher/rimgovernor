import {useEffect,useRef,useState} from 'react';
import {useController} from './useController';
import './SpatialPlan.css';
import ColonyNav from './ColonyNav';
type Plan={version?:number;summary:string;regions:any[];zones?:any[];build_phases?:any[];deviations?:any[];deferred:Record<string,string>};
export function phaseTitle(number:number,label:string){return `Phase ${number}: ${label.replace(/^\s*phase\s+\d+\s*(?:[:.\-–—]\s*)?/i,'').trim()}`;}
export function BasePlanPage(){
 const {state,error}=useController();
 return <SpatialPlan colony={state?.colony} layout={(state?.memory as any)?.spatial_layout} fullPage error={error}/>;
}
export default function SpatialPlan({layout,colony,fullPage=false,error}:{colony?:string;layout?:Plan;fullPage?:boolean;error?:string}){
 const [image,setImage]=useState(''),[status,setStatus]=useState('Loading map…'),[zoom,setZoom]=useState(1);
 const viewport=useRef<HTMLDivElement>(null);
 const key=JSON.stringify([colony,layout]);
 useEffect(()=>{
  if(!fullPage||!layout)return;
  const abort=new AbortController();let timer:ReturnType<typeof setTimeout>;let url='';
  async function load(){
   try{
    const response=await fetch('/api/spatial/image',{signal:abort.signal,cache:'no-store'});
    if(!response.ok)throw new Error('Map preview unavailable');
    const blob=await response.blob();if(abort.signal.aborted)return;
    url=URL.createObjectURL(blob);setImage(url);
   }catch{if(!abort.signal.aborted){setStatus('Map preview unavailable. Retrying…');timer=setTimeout(load,5000);}}
  }
  void load();return()=>{abort.abort();clearTimeout(timer);if(url)URL.revokeObjectURL(url);};
 },[key,fullPage]);
 function center(){const box=viewport.current;if(box){box.scrollTop=(box.scrollHeight-box.clientHeight)/2;box.scrollLeft=(box.scrollWidth-box.clientWidth)/2;}}
 if(!fullPage)return layout?<section className="mgr-card"><div className="mgr-card-title"><h2>Base plan</h2><a href="#base-plan">Open full-page map ↗</a></div><p>{layout.summary}</p><span className="mgr-small">{layout.zones?.length||0} planned areas · {layout.build_phases?.length||0} phases</span></section>:null;
 return <main className="base-plan-page">
  <header className="base-plan-toolbar"><ColonyNav active="base-plan"/><div className="base-plan-zoom"><button aria-label="Zoom out" onClick={()=>setZoom(z=>Math.max(.5,z-.25))}>−</button><input aria-label="Map zoom" type="range" min=".5" max="3" step=".25" value={zoom} onChange={e=>setZoom(Number(e.target.value))}/><button aria-label="Zoom in" onClick={()=>setZoom(z=>Math.min(3,z+.25))}>+</button><button onClick={()=>{setZoom(1);requestAnimationFrame(center);}}>Fit width</button></div></header>
  {!layout?<p role="status">{error||'No base plan yet. The architect will create it when automation starts.'}</p>:<div className="base-plan-body">
   <div className="base-plan-map" ref={viewport}>{image?<img src={image} alt="Long-term base map showing future zones, expansion directions, corridors and current footprints" style={{width:`${zoom*100}%`}} onLoad={center}/>:<p role="status">{status}</p>}</div>
   <aside className="base-plan-sidebar"><p>{layout.summary}</p><p className="mgr-small">Outer outlines reserve future space. Smaller footprints are current work. Gold squares are existing zones.</p>
    <h2>Build phases</h2>{layout.build_phases?.map(phase=><details key={phase.number} open={phase.number===1}><summary>{phaseTitle(phase.number,phase.label)}</summary><ul>{phase.goals.map((goal:string)=><li key={goal}>{goal}</li>)}</ul></details>)}
    <h2>Planned areas</h2>{layout.zones?.map(z=><details key={z.id}><summary>{z.label} <small>· Phase {z.phase}</small></summary><p>Starts {z.initial_size.width}×{z.initial_size.height}. Expands {z.expansion_direction} to {z.max_size.width}×{z.max_size.height}.</p><p>{z.rationale}</p></details>)}
    {!!layout.regions.length&&<h2>Current footprints</h2>}{layout.regions.map(r=><p key={r.id}><b>{r.label}</b> · {r.purpose}</p>)}
    {!!layout.deviations?.length&&<details><summary>Layout changes ({layout.deviations.length})</summary>{layout.deviations.map((d:any)=><p key={d.id}>{d.reason}</p>)}</details>}
    {Object.entries(layout.deferred).map(([id,reason])=><p key={id}>No site yet: {reason}</p>)}
   </aside>
  </div>}
 </main>;
}
