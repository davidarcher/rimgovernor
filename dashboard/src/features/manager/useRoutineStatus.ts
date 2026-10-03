import {useReading, type Reading} from '../useReading';
import {fetchRoutineStatus, type RoutineStatus} from './routineData';

// One polled reading of /api/routines shared by the panels that show its
// evidence.
export type RoutineReading = Reading<RoutineStatus>;
export const useRoutineStatus = (active: boolean): RoutineReading => useReading(active, fetchRoutineStatus, 3000, 'Routine diagnostics unavailable');
