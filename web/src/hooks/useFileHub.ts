import { useEffect, useRef } from 'react'
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useAuthStore } from '@/stores/auth'
import type { CommentEvent } from '@/types/collab'

/**
 * Live discussion of one file: connects to /hubs/notifications, calls JoinFile(fileId) (again after each
 * reconnect) and forwards "comment" events. LeaveFile + stop on unmount. Best effort: the list still
 * refreshes on focus / after own actions when the hub is unreachable.
 */
export function useFileHub(fileId: string, onEvent: (e: CommentEvent) => void, enabled = true): void {
  const handlerRef = useRef(onEvent)
  useEffect(() => {
    handlerRef.current = onEvent
  }, [onEvent])

  useEffect(() => {
    if (!enabled || !fileId) return
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/notifications', { accessTokenFactory: () => useAuthStore.getState().accessToken ?? '' })
      .withAutomaticReconnect([0, 2_000, 10_000, 30_000, 60_000])
      .configureLogging(LogLevel.Warning)
      .build()
    connection.on('comment', (e: CommentEvent) => handlerRef.current(e))
    const join = () => connection.invoke('JoinFile', fileId).catch(() => undefined)
    connection.onreconnected(() => void join())

    let disposed = false
    let retryTimer: number | undefined
    const start = () => {
      connection
        .start()
        .then(() => (disposed ? undefined : join()))
        .catch(() => {
          if (!disposed) retryTimer = window.setTimeout(start, 30_000)
        })
    }
    start()
    return () => {
      disposed = true
      window.clearTimeout(retryTimer)
      const stop = () => void connection.stop()
      if (connection.state === HubConnectionState.Connected) {
        connection.invoke('LeaveFile', fileId).catch(() => undefined).finally(stop)
      } else stop()
    }
  }, [fileId, enabled])
}
