import { useEffect } from 'react'
import { useLogout } from '@/hooks/useLogout'

/** Client-side inactivity limit (the server also expires idle sessions on refresh). */
const IDLE_TIMEOUT_MS = 30 * 60_000
const ACTIVITY_EVENTS = ['mousemove', 'mousedown', 'keydown', 'wheel', 'touchstart', 'scroll'] as const
/** Avoid resetting the timer on every mouse move. */
const THROTTLE_MS = 5_000
const LAST_ACTIVITY_KEY = 'bluedrive-last-activity'

/**
 * Signs the user out after 30 minutes without mouse/keyboard activity.
 * Activity is shared between tabs through localStorage so a busy tab keeps the others alive.
 */
export default function IdleLogout() {
  const logout = useLogout()

  useEffect(() => {
    let timer: number | undefined
    let lastMark = 0

    let lastActivity = Date.now()

    const readLast = () => {
      try {
        return Math.max(lastActivity, Number(localStorage.getItem(LAST_ACTIVITY_KEY)) || 0)
      } catch {
        return lastActivity
      }
    }

    const schedule = () => {
      window.clearTimeout(timer)
      const remaining = IDLE_TIMEOUT_MS - (Date.now() - readLast())
      timer = window.setTimeout(() => {
        // Another tab may have been active in the meantime.
        if (Date.now() - readLast() >= IDLE_TIMEOUT_MS) void logout('idle')
        else schedule()
      }, Math.max(remaining, 1_000))
    }

    const onActivity = () => {
      const now = Date.now()
      if (now - lastMark < THROTTLE_MS) return
      lastMark = now
      lastActivity = now
      try {
        localStorage.setItem(LAST_ACTIVITY_KEY, String(now))
      } catch {
        // Storage unavailable: the timer still works per tab.
      }
      schedule()
    }

    onActivity()
    ACTIVITY_EVENTS.forEach((e) => window.addEventListener(e, onActivity, { passive: true }))
    return () => {
      window.clearTimeout(timer)
      ACTIVITY_EVENTS.forEach((e) => window.removeEventListener(e, onActivity))
    }
  }, [logout])

  return null
}
