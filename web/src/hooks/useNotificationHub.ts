import { useEffect, useRef } from 'react'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { useAuthStore } from '@/stores/auth'
import type { NotificationDto } from '@/types/admin'

export interface NotificationHubHandlers {
  /** A personal notification for the signed-in user. */
  notification?: (n: NotificationDto) => void
  /** A system announcement was added, changed or removed (sent to every signed-in user). */
  announcement?: () => void
}

/**
 * Connect to the SignalR hub /hubs/notifications while signed in and dispatch its events to `handlers`.
 * The token is read lazily so refreshed tokens are used on reconnect.
 */
export function useNotificationHub(handlers: NotificationHubHandlers, enabled: boolean): void {
  const handlersRef = useRef(handlers)
  useEffect(() => {
    handlersRef.current = handlers
  }, [handlers])

  useEffect(() => {
    if (!enabled) return
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/notifications', { accessTokenFactory: () => useAuthStore.getState().accessToken ?? '' })
      .withAutomaticReconnect([0, 2_000, 10_000, 30_000, 60_000])
      .configureLogging(LogLevel.Warning)
      .build()
    connection.on('notification', (n: NotificationDto) => handlersRef.current.notification?.(n))
    connection.on('announcement', () => handlersRef.current.announcement?.())
    // Realtime is best effort: unread-count polling keeps the bell up to date while the hub is unreachable.
    // Automatic reconnect only covers drops after a successful start, so retry the initial start ourselves
    // (e.g. the access token was being refreshed at that moment).
    let disposed = false
    let retryTimer: number | undefined
    let starting: Promise<void> = Promise.resolve()
    const start = () => {
      starting = connection.start().catch(() => {
        if (!disposed) retryTimer = window.setTimeout(start, 30_000)
      })
    }
    start()
    return () => {
      disposed = true
      window.clearTimeout(retryTimer)
      // Stopping while the connection is still starting logs "Failed to start the HttpConnection before stop()".
      void starting.finally(() => connection.stop())
    }
  }, [enabled])
}
