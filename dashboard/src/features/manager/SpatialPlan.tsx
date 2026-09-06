import {useEffect,useState} from 'react';
export default function SpatialPlan({layout,colony}:{colony?:string;layout?:{signature:string;summary:string;regions:any[];deferred:Record<string,string>}}){
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
 return <section className="mgr-card"><div className="mgr-card-title"><h2>Base layout</h2><button onClick={()=>setOpen(!open)}>{open?'Collapse':'Show map'}</button></div><p>{layout.summary}</p>{open&&<>{image?<img src={image} alt="Coordinate-labelled base plan with reserved project footprints" style={{width:'100%',maxHeight:650,objectFit:'contain',imageRendering:'pixelated'}}/>:<p role="status">{status}</p>}<p className="mgr-small">Colored outlines reserve sites. Gold squares are existing zones. These are plans, not finished buildings.</p>{layout.regions.map(r=><p key={r.id}><b>{r.label.replace(/^RimBot\s*:\s*/i,'').replace(/\s+for\s+project\s+\S+\s*$/i,'')}</b> · {r.purpose}<br/><span className="mgr-small">{r.rationale}</span></p>)}{Object.entries(layout.deferred).map(([id,reason])=><p key={id}>No site yet: {reason}</p>)}</>}</section>;
}
