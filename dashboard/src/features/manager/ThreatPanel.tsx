import {useDecoded, type Reading} from '../useReading';
import {readThreatStatus} from './threatData';

// Raid points and the wealth split the storyteller scales them by (#395):
// evidence from the live colony census, read every few seconds while the
// Colony view is open. Hidden when the service does not serve the census.
const silver = (v: number | null) => v === null ? '—' : Math.round(v).toLocaleString();
export default function ThreatPanel({colony}: {colony: Reading<unknown>}) {
  const state = useDecoded(colony, readThreatStatus);
  if (state.hidden) return null;
  const v = state.value;
  return <section className="observation-panel" aria-label="Raid threat"><h2>Raid threat</h2>
    {state.stale && <p role="status">{v ? 'Stale — last recorded census. ' : 'Unavailable. '}{state.error || 'Waiting for the colony census.'}</p>}
    <p className="observation-value">{!v ? '�' : v.raidPoints === null ? 'Raid points unknown' : `${silver(v.raidPoints)} raid points`}</p>
    {v && <dl className="observation-identity">
      <div><dt>Wealth</dt><dd>{silver(v.wealthTotal)}</dd></div>
      <div><dt>Items</dt><dd>{silver(v.wealthItems)}</dd></div>
      <div><dt>Buildings</dt><dd>{silver(v.wealthBuildings)}</dd></div>
      <div><dt>Pawns</dt><dd>{silver(v.wealthPawns)}</dd></div>
      <div><dt>Build tier</dt><dd>{v.buildTier ?? '—'}{v.playerTechLevel ? ` (${v.playerTechLevel} faction)` : ''}</dd></div>
    </dl>}
    {v && v.shrines && v.shrines.length > 0 && <dl className="observation-identity" aria-label="Ancient shrines">
      {v.shrines.map(shrine => <div key={shrine.id}><dt>Shrine {shrine.id}</dt><dd>{shrine.sealed ? 'sealed' : shrine.guardsAlive ? 'breached, guards alive' : 'cleared'}, {shrine.filledCaskets}/{shrine.caskets} caskets filled{shrine.inHome ? ', in Home' : ''}{shrine.ready === null ? '' : shrine.ready ? ` � breach ready (${shrine.squad} armed, ${shrine.traps} traps)` : ` � hold: ${shrine.reason}`}</dd></div>)}
    </dl>}
    <p className="observation-note">Points a default threat incident would draw at tick {v ? v.tick.toLocaleString() : '—'}, as the game computes them from colony wealth, colonists, adaptation and difficulty.</p>
  </section>;
}
