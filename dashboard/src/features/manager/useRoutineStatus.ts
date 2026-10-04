import {useReading, type Reading} from '../useReading';
import {fetchRoutineStatus, type RoutineStatus} from './routineData';

const routineIntervalMs = 3000;

// One polled reading of /api/routines shared by the panels that show its
// evidence.
type RoutineReading = Reading<RoutineStatus>;
export const useRoutineStatus = (active: boolean): RoutineReading => useReading(active, fetchRoutineStatus, routineIntervalMs, 'Routine diagnostics unavailable');
