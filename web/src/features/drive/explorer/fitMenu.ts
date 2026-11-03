import type { CSSProperties } from 'react'

const MARGIN = 12

/**
 * Placement and height for a menu opened at a point (or under / above an anchor): it opens on the side with more room and
 * never grows past the window — longer menus scroll inside (small windows, items near the bottom of the list).
 */
export function fitMenu(top: number, bottom = top): { placement: 'bottomLeft' | 'topLeft'; style: CSSProperties } {
  const below = window.innerHeight - bottom - MARGIN
  const above = top - MARGIN
  const down = below >= above || below >= 360
  return { placement: down ? 'bottomLeft' : 'topLeft', style: { maxHeight: Math.max(120, down ? below : above), overflowY: 'auto' } }
}
