import BridgeColony from './features/manager/BridgeColony';
import LocalColonies from './features/manager/LocalColonies';
import ScenarioWatch from './features/manager/ScenarioWatch';
export default function App(){return location.pathname === '/colonies'
  ? <main className="colony-directory"><LocalColonies/></main>
  : location.pathname === '/scenario' ? <ScenarioWatch/> : <BridgeColony/>;}
