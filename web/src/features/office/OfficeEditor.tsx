import { useEffect, useId, useRef, useState } from 'react'
import { Button, Result, Spin } from 'antd'
import { ReloadOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import type { OfficeEditorConfig } from '@/types/collab'
import '@/styles/collab.css'

interface DocEditorInstance {
  destroyEditor?: () => void
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
