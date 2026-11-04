import { useEffect, useId, useRef, useState } from 'react'
import { App, Button, Result, Spin } from 'antd'
import { ReloadOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import type { OfficeEditorConfig } from '@/types/collab'
import '@/styles/collab.css'

interface DocEditorInstance {
  destroyEditor?: () => void
}

/** Payload of the DocsAPI onError / onWarning events. */
interface EditorEvent {
  data?: { errorCode?: number; errorDescription?: string; warningCode?: number; warningDescription?: string }
}

declare global {
  interface Window {
    DocsAPI?: { DocEditor: new (placeholderId: string, config: Record<string, unknown>) => DocEditorInstance }
  }
}

/** One <script> per Document Server URL, shared by every editor on the page. Failed loads can be retried. */
const scripts = new Map<string, Promise<void>>()

function loadScript(url: string): Promise<void> {
  const cached = scripts.get(url)
  if (cached) return cached
  const promise = new Promise<void>((resolve, reject) => {
    const el = document.createElement('script')
    el.src = url
    el.async = true
    el.onload = () => (window.DocsAPI ? resolve() : reject(new Error('DocsAPI missing')))
    el.onerror = () => {
      el.remove()
      reject(new Error('script load failed'))
    }
    document.head.appendChild(el)
  })
  promise.catch(() => scripts.delete(url))
  scripts.set(url, promise)
  return promise
}

interface OfficeEditorProps {
  config: OfficeEditorConfig
  /** Called after a failure when the user asks to retry (e.g. refetch a fresh config). */
  onRetry?: () => void
}

type Status = 'loading' | 'ready' | 'failed'

/**
 * ONLYOFFICE editor host: loads the Document Server api.js once, creates DocsAPI.DocEditor in a
 * placeholder created outside React (the API replaces it with an iframe) and destroys it on unmount.
 */
export default function OfficeEditor({ config, onRetry }: OfficeEditorProps) {
  const { t } = useTranslation()
  const hostRef = useRef<HTMLDivElement>(null)
  const placeholderId = `bd-office-${useId().replace(/[^a-zA-Z0-9]/g, '')}`
  const [state, setState] = useState<{ status: Status; attempt: number }>({ status: 'loading', attempt: 0 })
  const attempt = state.attempt
  const { notification } = App.useApp()
  // Document Server errors (e.g. the community edition's 20-connection limit) surface as a Vietnamese notice;
  // kept in a ref so a new translator does not recreate the editor.
  const notifyRef = useRef<(kind: 'error' | 'warning', e: EditorEvent) => void>(() => {})
  useEffect(() => {
    notifyRef.current = (kind, e) => {
      const description = e?.data?.errorDescription || e?.data?.warningDescription || ''
      const code = e?.data?.errorCode ?? e?.data?.warningCode ?? ''
      if (kind === 'error')
        notification.error({ key: placeholderId, message: t('collab.office.editorErrorTitle'), description: t('collab.office.editorError', { description, code }), duration: 0 })
      else notification.warning({ key: placeholderId, message: t('collab.office.editorWarningTitle'), description: `${description} (${code})` })
    }
  }, [notification, t, placeholderId])

  useEffect(() => {
    const host = hostRef.current
    if (!host) return
    let disposed = false
    let editor: DocEditorInstance | null = null
    const placeholder = document.createElement('div')
    placeholder.id = placeholderId
    host.appendChild(placeholder)

    loadScript(config.apiScriptUrl)
      .then(() => {
        if (disposed || !window.DocsAPI) return
        editor = new window.DocsAPI.DocEditor(placeholderId, {
          width: '100%',
          height: '100%',
          ...config.config,
          events: {
            onError: (e: EditorEvent) => notifyRef.current('error', e),
            onWarning: (e: EditorEvent) => notifyRef.current('warning', e),
          },
        })
        // The editor iframe shows its own loading screen from here on.
        setState((s) => ({ ...s, status: 'ready' }))
      })
      .catch(() => {
        if (!disposed) setState((s) => ({ ...s, status: 'failed' }))
      })

    return () => {
      disposed = true
      try {
        editor?.destroyEditor?.()
      } catch {
        // The iframe may already be gone with the page.
      }
      host.replaceChildren()
    }
  }, [config, placeholderId, attempt])

  return (
    <div className="bd-office-frame">
      <div ref={hostRef} className="bd-office-host" />
      {state.status === 'loading' && (
        <div className="bd-office-overlay">
          <Spin size="large" tip={t('collab.office.loading')}>
            <div style={{ width: 120, height: 60 }} />
          </Spin>
        </div>
      )}
      {state.status === 'failed' && (
        <div className="bd-office-overlay">
          <Result
            status="warning"
            title={t('collab.office.unavailableTitle')}
            subTitle={t('collab.office.unavailable')}
            extra={
              <Button
                type="primary"
                icon={<ReloadOutlined />}
                onClick={() => {
                  setState((s) => ({ status: 'loading', attempt: s.attempt + 1 }))
                  onRetry?.()
                }}
              >
                {t('collab.office.retry')}
              </Button>
            }
          />
        </div>
      )}
    </div>
  )
}
