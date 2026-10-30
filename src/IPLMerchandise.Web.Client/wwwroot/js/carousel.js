// Horizontal carousel helpers for TeamCarousel.razor.
export function scrollPage(track, direction) {
  track.scrollBy({ left: direction * track.clientWidth * 0.9, behavior: 'smooth' });
}

// Reports whether the track is at its start/end (for disabling the arrows) on scroll and resize.
// A few pixels of tolerance: scroll snapping can leave the first or last tile a couple of pixels from the edge.
const EDGE_TOLERANCE = 4;
export function watch(track, dotnet) {
  const report = () => dotnet.invokeMethodAsync('OnScrolled',
    track.scrollLeft <= EDGE_TOLERANCE,
    track.scrollLeft + track.clientWidth >= track.scrollWidth - EDGE_TOLERANCE);
  track.addEventListener('scroll', report, { passive: true });
  window.addEventListener('resize', report);
  report();
  return {
    dispose() {
      track.removeEventListener('scroll', report);
      window.removeEventListener('resize', report);
    }
  };
}
