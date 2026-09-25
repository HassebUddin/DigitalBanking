export const skeletonHoldMs = 800;

export function holdSkeleton(startedAt: number, reveal: () => void) {
  const elapsed = startedAt > 0 ? Date.now() - startedAt : 0;
  const wait = Math.max(0, skeletonHoldMs - elapsed);
  window.setTimeout(reveal, wait);
}
