// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { afterEach, it, expect } from 'vitest';
import VisualReviews, { type VisualReview } from './VisualReviews';
afterEach(cleanup);
const review: VisualReview = { id:'abc', question:'Check the shell', current_load:false,
  source:{tick:10,width:1280,height:720,captured_at:1},
  report:{answer:'Check access',missing_facts:['Door state'],concerns:[{observation:'Possible gap',
    region:{left:.1,top:.2,right:.5,bottom:.6},confidence:'low',verify:'Read the perimeter'}]} };
it('anchors numbered concerns to the retained source and preserves expansion on refresh', () => {
  const {rerender} = render(<VisualReviews reviews={[review]} />);
  fireEvent.click(screen.getByText('Check the shell'));
  const img = screen.getByAltText('Original screenshot for: Check the shell');
  expect(img.getAttribute('src')).toBe('/api/visual-reviews/abc/source');
  expect(screen.getByLabelText('Concern 1: Possible gap').style.left).toBe('10%');
  expect(screen.getByText(/previous load/)).toBeTruthy();
  rerender(<VisualReviews reviews={[{...review}]} />);
  expect(screen.getByText('Check the shell').closest('details')?.open).toBe(true);
  fireEvent.error(img);
  expect(screen.getByText(/Source image unavailable/)).toBeTruthy();
  expect(screen.queryByLabelText('Concern 1: Possible gap')).toBeNull();
});
