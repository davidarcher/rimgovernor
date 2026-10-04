import {getJSON} from '../http';
import {useReading, type Reading} from '../useReading';

// /api/player/colony feeds the food plan and the raid threat; the Colony view
// reads it once and each panel decodes its own slice.
const colonyIntervalMs = 5000;
export const useColony = (active: boolean): Reading<unknown> => useReading(active, signal => getJSON('/api/player/colony', signal, 'Colony status unavailable'), colonyIntervalMs, 'Colony status unavailable');
