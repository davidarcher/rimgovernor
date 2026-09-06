import {useState} from 'react';
export default function SpatialPlan({layout}:{layout?:{signature:string;summary:string;regions:any[];deferred:Record<string,string>}}){
 const [open,setOpen]=useState(true);
 if(!layout)return null;
 return <section className="mgr-card"><div className="mgr-card-title"><h2>Base layout</h2><button onClick={()=>setOpen(!open)}>{open?'Collapse':'Show map'}</button></div><p>{layout.summary}</p>{open&&<><img src={'/api/spatial/image?v='+layout.signature} alt="Coordinate-labelled base plan with reserved project footprints" style={{width:'100%',maxHeight:650,objectFit:'contain',imageRendering:'pixelated'}}/><p className="mgr-small">Colored outlines reserve sites. Gold squares are existing zones. These are plans, not finished buildings.</p>{layout.regions.map(r=><p key={r.id}><b>{r.label}</b> · {r.purpose}<br/><span className="mgr-small">{r.rationale}</span></p>)}{Object.entries(layout.deferred).map(([id,reason])=><p key={id}>No site yet: {reason}</p>)}</>}</section>;
}
