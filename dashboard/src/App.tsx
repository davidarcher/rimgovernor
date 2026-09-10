import BridgeColony from './features/manager/BridgeColony';
import LocalColonies from './features/manager/LocalColonies';
export default function App(){return location.pathname === '/colonies'
  ? <main className="colony-directory"><LocalColonies/></main> : <BridgeColony/>;}
