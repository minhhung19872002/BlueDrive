import { useEffect, useRef } from 'react'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { useAuthStore } from '@/stores/auth'
import type { NotificationDto } from '@/types/admin'

/**
 * Connect to the SignalR hub /hubs/notifications while signed in and call `onNotification`
 * for each "notification" event. The token is read lazily so refreshed tokens are used on reconnect.
 */
export function useNotificationHub(onNotification: (n: NotificationDto) => void, enabled: boolean): void {
  const handlerRef = useRef(onNotification)
  useEffect(() => {
    handlerRef.current = onNotification
  }, [onNotification])

  useEffect(() => {
    if (!enabled) return
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/notifications', { accessTokenFactory: () => useAuthStore.getState().accessToken ?? '' })
      .withAutomaticReconnect([0, 2_000, 10_000, 30_000, 60_000])
      .configureLogging(LogLevel.Warning)
      .build()
    connection.on('notification', (n: NotificationDto) => handlerRef.current(n))
    // Realtime is best effort: unread-count polling keeps the bell up to date while the hub is unreachable.
    // Automatic reconnect only covers drops after a successful start, so retry the initial start ourselves
    // (e.g. the access token was being refreshed at that moment).
    let disposed = false
    let retryTimer: number | undefined
    const start = () => {
      connection.start().catch(() => {
        if (!disposed) retryTimer = window.setTimeout(start, 30_000)
      })
    }
    start()
    return () => {
      disposed = true
      window.clearTimeout(retryTimer)
      void connection.stop()
    }
  }, [enabled])
}
