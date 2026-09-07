import {useEffect,useState} from 'react';
export default function SpatialPlan({layout,colony}:{colony?:string;layout?:{version?:number;summary:string;regions:any[];zones?:any[];build_phases?:any[];deviations?:any[];deferred:Record<string,string>}}){
 const [open,setOpen]=useState(true);
 const [image,setImage]=useState('');
 const [status,setStatus]=useState('Loading map…');
 const key=JSON.stringify([colony,layout]);
 useEffect(()=>{
  const abort=new AbortController();let timer:ReturnType<typeof setTimeout>;let url='';
  setImage('');setStatus('Loading map…');
  async function load(){
   try{
    const response=await fetch('/api/spatial/image',{signal:abort.signal,cache:'no-store'});
    if(!response.ok)throw new Error('Map preview unavailable. Retrying…');
    const blob=await response.blob();if(abort.signal.aborted)return;
    url=URL.createObjectURL(blob);setImage(url);
   }catch(error){if(!abort.signal.aborted){setStatus('Map preview unavailable. Retrying…');timer=setTimeout(load,5000);}}
  }
  if(layout)void load();
  return ()=>{abort.abort();clearTimeout(timer);if(url)URL.revokeObjectURL(url);};
 },[key]);
 if(!layout)return null;
 return <section className="mgr-card"><div className="mgr-card-title"><h2>Base plan</h2><button onClick={()=>setOpen(!open)}>{open?'Collapse':'Show map'}</button></div><p>{layout.summary}</p>{open&&<>{image?<img src={image} alt="Coordinate-labelled base plan with reserved project footprints" style={{width:'100%',maxHeight:650,objectFit:'contain',imageRendering:'pixelated'}}/>:<p role="status">{status}</p>}<p className="mgr-small">Outer outlines reserve future space; smaller footprints are current work. Gold squares are existing zones.</p>{layout.build_phases?.map(phase=><details key={'phase-'+phase.number}><summary><b>Phase {phase.number}: {phase.label}</b></summary><ul>{phase.goals.map((goal:string)=><li key={goal}>{goal}</li>)}</ul></details>)}{layout.zones?.map(z=><p key={'zone-'+z.id}><b>{z.label}</b> · Phase {z.phase}<br/><span className="mgr-small">Starts {z.initial_size.width}×{z.initial_size.height}; room to grow {z.expansion_direction} to {z.max_size.width}×{z.max_size.height}.</span></p>)}{!!layout.deviations?.length&&<p className="mgr-small">{layout.deviations.length} recorded layout changes inform future reviews.</p>}{layout.regions.map(r=><p key={r.id}><b>{r.label.replace(/^RimBot\s*:\s*/i,'').replace(/\s+for\s+project\s+\S+\s*$/i,'')}</b> · {r.purpose}<br/><span className="mgr-small">{r.rationale}</span></p>)}{Object.entries(layout.deferred).map(([id,reason])=><p key={id}>No site yet: {reason}</p>)}</>}</section>;
}
