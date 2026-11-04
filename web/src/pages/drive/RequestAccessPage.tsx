import { Button, Result, Skeleton } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Navigate, useNavigate, useParams } from 'react-router-dom'
import { getAccessInfo } from '@/api/permissions'
import RequestAccess from '@/features/sharing/RequestAccess'

/** Spec 1.2 / 5.2: a shortcut (or address) to an item one may not open: "Bạn cần quyền truy cập" with the request form. */
export default function RequestAccessPage() {
  const { id = '' } = useParams<{ id: string }>()
  const { t } = useTranslation()
  const navigate = useNavigate()
  const info = useQuery({ queryKey: ['access-info', id], queryFn: () => getAccessInfo(id), retry: 0, enabled: !!id })
  const home = <Button onClick={() => navigate('/')}>{t('requestAccess.home')}</Button>
  if (info.isLoading) return <Skeleton active />
  if (!info.data) return <Result status="404" title={t('requestAccess.gone')} extra={home} />
  if (info.data.hasAccess) return <Navigate to={`/files/${id}`} replace />
  return <RequestAccess info={info.data} extra={home} />
}
