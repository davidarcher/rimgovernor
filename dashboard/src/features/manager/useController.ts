import {useCallback, useEffect, useRef, useState} from 'react';

export interface ControllerState {
  connected:boolean; mode:'manual'|'automate'; busy:boolean; colony:string; started_at:number|null;
  status:Record<string,any>; counters:Record<string,number>; settings:Record<string,any>;
  observation:Record<string,any>; memory:{plans:Record<string,any>|null; goals:any[]; work:any[]; chat:any[]};
  activity:any[]; capabilities:number; events_connected:boolean;
}

export async function command(path:string, body?:unknown, method='POST') {
  const r=await fetch('/api/'+path,{method,headers:{'Content-Type':'application/json','X-RimBot':'1'},body:body===undefined?undefined:JSON.stringify(body)});
  const data=await r.json();
  if(!r.ok) throw new Error(typeof data.detail==='string'?data.detail:JSON.stringify(data.detail));
  return data;
}

export function useController() {
  const [state,setState]=useState<ControllerState|null>(null);
  const [error,setError]=useState('');
  const pending=useRef(false);
  const refresh=useCallback(async()=>{
    if(pending.current)return;
    pending.current=true;
    try{const r=await fetch('/api/state');if(!r.ok)throw Error('Controller unavailable');setState(await r.json());}
    catch(e){setError(String(e));}finally{pending.current=false;}
  },[]);
  useEffect(()=>{refresh();const interval=setInterval(refresh,2000);const events=new EventSource('/api/events');events.onmessage=refresh;return()=>{clearInterval(interval);events.close();};},[refresh]);
  const act=async(path:string,body?:unknown,method='POST')=>{try{await command(path,body,method);await refresh();return true;}catch(e){setError(e instanceof Error?e.message:String(e));return false;}};
  return {state,error,setError,act,refresh};
}
