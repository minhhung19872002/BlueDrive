import type { ReactNode } from 'react'
import { Result, Spin } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getErrorMessage } from '@/api/errors'
import { getPublicOfficeConfig } from '@/api/office'
import { qkc } from '@/api/queryKeys.collab'
import OfficeEditor from './OfficeEditor'

interface PublicOfficeViewerProps {
  token: string
  access: string
  /** File inside a shared folder; omitted for a shared file. */
  fileId?: string | null
  /** Shown under the error (e.g. a download button). */
  extra?: ReactNode
}

/** Read-only ONLYOFFICE view of an Office file on the public share page (/s/:token). */
export default function PublicOfficeViewer({ token, access, fileId, extra }: PublicOfficeViewerProps) {
  const { t } = useTranslation()
  const config = useQuery({
    queryKey: [...qkc.publicOfficeConfig(token, fileId ?? null), access],
    queryFn: () => getPublicOfficeConfig(token, access, fileId),
    retry: 0,
    staleTime: Infinity,
    gcTime: 0,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  })
  if (config.isError)
    return <Result status="warning" title={t('collab.office.openFailed')} subTitle={getErrorMessage(config.error)} extra={extra} />
  if (!config.data) return <Spin size="large" />
  return <OfficeEditor config={config.data} onRetry={() => void config.refetch()} />
}
