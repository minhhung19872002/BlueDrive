import { useState } from 'react'
import { Alert, Button, Card, Result, Space, Typography } from 'antd'
import { DesktopOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { authorizeDesktop } from '@/api/devices'
import { getErrorMessage } from '@/api/errors'
import AuthBrand from '@/components/AuthBrand'
import { useAuthStore } from '@/stores/auth'

/** Loopback redirect accepted for the desktop app (RFC 8252): http://127.0.0.1:{port}/… or localhost. */
function isLoopback(uri: string | null): uri is string {
  if (!uri) return false
  try {
    const u = new URL(uri)
    return u.protocol === 'http:' && ['127.0.0.1', 'localhost', '[::1]'].includes(u.hostname) && !!u.port && !u.search && !u.hash
  } catch {
    return false
  }
}

/**
 * BlueDrive Desktop sign-in (OAuth 2.0 authorization code + PKCE). The app opens this page in the system browser; the signed-in
 * user allows access and the browser returns to the app's loopback address with a one-time code.
 */
export default function ConnectAuthorizePage() {
  const { t } = useTranslation()
  const [params] = useSearchParams()
  const user = useAuthStore((s) => s.user)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState(false)

  const clientId = params.get('client_id')
  const redirectUri = params.get('redirect_uri')
  const challenge = params.get('code_challenge')
  const method = params.get('code_challenge_method')
  const state = params.get('state')
  const deviceName = params.get('device_name')
  const valid = clientId === 'bluedrive-desktop' && isLoopback(redirectUri) && !!challenge && method === 'S256'

  const back = (query: string) => {
    if (!isLoopback(redirectUri)) return
    window.location.assign(`${redirectUri}?${query}${state ? `&state=${encodeURIComponent(state)}` : ''}`)
  }

  const allow = async () => {
    if (!valid) return
    setBusy(true)
    setError(null)
    try {
      const r = await authorizeDesktop({
        clientId: clientId!,
        redirectUri: redirectUri!,
        codeChallenge: challenge!,
        codeChallengeMethod: method!,
        state,
        deviceName,
      })
      setDone(true)
      window.location.assign(r.redirectUrl)
    } catch (e) {
      setError(getErrorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="bd-auth-page">
      <Card style={{ width: '100%', maxWidth: 460 }}>
        <AuthBrand />
        {!valid ? (
          <Result status="warning" title={t('connect.invalidTitle')} subTitle={t('connect.invalidHint')} />
        ) : done ? (
          <Result status="success" title={t('connect.doneTitle')} subTitle={t('connect.doneHint')} />
        ) : (
          <Space direction="vertical" size="middle" style={{ width: '100%' }}>
            <Typography.Title level={4} style={{ textAlign: 'center', marginBottom: 0 }}>
              <DesktopOutlined /> {t('connect.title')}
            </Typography.Title>
            <Typography.Paragraph style={{ textAlign: 'center' }}>
              {t('connect.question', {
                user: user?.fullName ?? '',
                device: deviceName || t('connect.thisComputer'),
              })}
            </Typography.Paragraph>
            <Alert type="info" showIcon message={t('connect.scope')} />
            {error && <Alert type="error" showIcon message={error} />}
            <Space style={{ width: '100%', justifyContent: 'center' }}>
              <Button onClick={() => back('error=access_denied')}>{t('connect.deny')}</Button>
              <Button type="primary" loading={busy} onClick={() => void allow()}>
                {t('connect.allow')}
              </Button>
            </Space>
          </Space>
        )}
      </Card>
    </div>
  )
}
