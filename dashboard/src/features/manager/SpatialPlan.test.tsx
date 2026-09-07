import {expect,it} from 'vitest';
import {phaseTitle} from './SpatialPlan';
it('shows a phase number exactly once for generated and plain labels',()=>{
 expect(phaseTitle(2,'Phase 2: Stability')).toBe('Phase 2: Stability');
 expect(phaseTitle(2,'phase 2 — Stability')).toBe('Phase 2: Stability');
 expect(phaseTitle(2,'Stability')).toBe('Phase 2: Stability');
});
