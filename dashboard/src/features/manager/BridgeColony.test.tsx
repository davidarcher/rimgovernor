// @vitest-environment jsdom
import {render,screen,fireEvent,waitFor,cleanup} from '@testing-library/react';
import {afterEach,it,expect,vi} from 'vitest';
import BridgeColony from './BridgeColony';
afterEach(()=>{cleanup();vi.unstubAllGlobals();location.hash='';});
it('keeps project details off the main view and sends Enter without navigating',async()=>{
 const requests:{url:string;options:any}[]=[];
 vi.stubGlobal('fetch',vi.fn(async(url:string,options:any)=>{requests.push({url,options});return {ok:true,json:async()=>url==='/api/state'?{sessionId:'a',connected:true,mode:'manual',mood:'happy',cameraVersion:0,goals:{long:'Build a lasting settlement',short:'Store the meals'},feed:[],status:{label:'Manual'},game:{paused:true,stale:false},counters:{tools:0,actions:0,model_calls:0}}:{events:[]}};}));
 render(<BridgeColony/>);
 await screen.findByText('Store the meals');
 expect(screen.queryByText('Build a lasting settlement')).toBeNull();
 const input=screen.getByLabelText('Message the colony manager');fireEvent.change(input,{target:{value:'Protect the supplies'}});fireEvent.keyDown(input,{key:'Enter'});
 await waitFor(()=>expect(requests.some(r=>r.url==='/api/chat'&&JSON.parse(r.options.body).text==='Protect the supplies')).toBe(true));
 expect(location.hash).toBe('');
 fireEvent.click(screen.getByRole('button',{name:'Pause video'}));
 await waitFor(()=>expect(requests.some(r=>r.url==='/api/video'&&JSON.parse(r.options.body).playing===false)).toBe(true));
 expect(screen.getByRole('button',{name:'Play video'})).toBeTruthy();
 location.hash='projects';fireEvent(window,new Event('hashchange'));
 await screen.findByText('Build a lasting settlement');
 expect(screen.queryByLabelText('Message the colony manager')).toBeNull();
});
