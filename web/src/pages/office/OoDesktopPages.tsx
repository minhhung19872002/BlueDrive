import { useEffect, useRef, useState } from 'react'
import { Result, Spin } from 'antd'
import { useTranslation } from 'react-i18next'
import { Navigate, useNavigate, useSearchParams } from 'react-router-dom'
import { logout as apiLogout } from '@/api/auth'
import { getErrorMessage } from '@/api/errors'
import { ooLogin, ooLogout, redeemTicket } from '@/features/office/ooDesktop'
import { useAuthStore } from '@/stores/auth'

/** "entryPage" of the ONLYOFFICE Desktop Editors provider: once signed in, BlueDrive joins "Connected clouds" and shows the documents. */
export function OoDesktopEntryPage() {
  const user = useAuthStore((s) => s.user)
  useEffect(() => {
    if (user) ooLogin(user)
  }, [user])
  return <Navigate to="/my-drive" replace />
}

/** "/oo-desktop/open?ticket=…": opened by BlueDrive Desktop ("Cùng soạn thảo bằng ONLYOFFICE"); signs in with the ticket, opens the editor. */
export function OoDesktopOpenPage() {
  const { t } = useTranslation()
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const setSession = useAuthStore((s) => s.setSession)
  const ticket = params.get('ticket')
  const [error, setError] = useState<string | null>(ticket ? null : t('ooDesktop.noTicket'))
  const started = useRef(false)

  useEffect(() => {
    if (started.current || !ticket) return
    started.current = true
    redeemTicket(ticket)
      .then(({ auth, next }) => {
        setSession(auth.accessToken, auth.refreshToken, auth.user)
        ooLogin(auth.user)
        navigate(next, { replace: true })
      })
      .catch((e) => setError(getErrorMessage(e)))
  }, [navigate, setSession, ticket])

  return error ? (
    <Result status="warning" title={t('ooDesktop.openFailed')} subTitle={error} />
  ) : (
    <Spin size="large" tip={t('ooDesktop.opening')} fullscreen />
  )
}

/** "extraLogout" of the provider: signs out of BlueDrive and removes it from "Connected clouds". */
export function OoDesktopLogoutPage() {
  const logout = useAuthStore((s) => s.logout)
  const [done, setDone] = useState(false)
  useEffect(() => {
    apiLogout()
      .catch(() => undefined)
      .finally(() => {
        logout()
        ooLogout()
        setDone(true)
      })
  }, [logout])
  return done ? <Navigate to="/login" replace /> : <Spin fullscreen />
}
