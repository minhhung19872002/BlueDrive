import { Button, Result, Spin, Tag, Tooltip, Typography } from 'antd'
import { CloseOutlined, EditOutlined, EyeOutlined } from '@ant-design/icons'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { getErrorMessage } from '@/api/errors'
import { getOfficeConfig } from '@/api/office'
import { qk } from '@/api/queryKeys'
import { qkc } from '@/api/queryKeys.collab'
import FileTypeIcon from '@/features/drive/components/FileTypeIcon'
import { useFileDetail } from '@/features/drive/hooks'
import OfficeEditor from '@/features/office/OfficeEditor'
import { folderUrl } from '@/features/drive/utils'
import { isOfficeEditable } from '@/features/office/officeFiles'
import { useAuthStore } from '@/stores/auth'
import '@/styles/collab.css'

/** Full-screen ONLYOFFICE editor /files/:id/edit (#21, #41, #42). ?view=true forces read-only. */
export default function OfficeEditorPage() {
  const { id = '' } = useParams<{ id: string }>()
  const [params] = useSearchParams()
  // ?version=… opens an older version (read-only), from "Phiên bản" → Xem.
  const version = params.get('version')
  const forceView = params.get('view') === 'true' || !!version
  const { t } = useTranslation()
  const navigate = useNavigate()
  const location = useLocation()
  const queryClient = useQueryClient()
  const detail = useFileDetail(id)
  const config = useQuery({
    queryKey: qkc.officeConfig(id, forceView, version),
    queryFn: () => getOfficeConfig(id, forceView, version),
    enabled: !!id,
    retry: 0,
    // A fresh signed config each time the page opens; never refetch under a running editor.
    staleTime: Infinity,
    gcTime: 0,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  })

  const personal = useAuthStore((s) => s.user?.personalWorkspaceId)
  const close = () => {
    // Saved versions arrive through the Document Server callback: refresh the file views.
    void queryClient.invalidateQueries({ queryKey: qk.drive })
    // Back to where the editor was opened from; opened directly (link, reload) → the file's folder.
    if (location.key !== 'default') navigate(-1)
    else navigate(detail.data ? folderUrl(detail.data.workspaceId, detail.data.folderId, personal) : `/files/${id}`)
  }

  const f = detail.data
  const editing = config.data?.mode === 'edit'

  let body
  if (config.isError)
    body = <Result status="warning" title={t('collab.office.openFailed')} subTitle={getErrorMessage(config.error)} />
  else if (config.data) body = <OfficeEditor config={config.data} onRetry={() => void config.refetch()} />
  else
    body = (
      <div className="bd-office-overlay">
        <Spin size="large" />
      </div>
    )

  return (
    <div className="bd-office-page">
      <div className="bd-office-bar">
        {f &&<FileTypeIcon type="file" mediaType={f.mediaType} extension={f.extension} size={20} />}
        <Typography.Text strong ellipsis className="bd-office-title" title={f?.name}>
          {f?.name ?? ''}
        </Typography.Text>
        {config.data && (
          <Tag icon={editing ? <EditOutlined /> : <EyeOutlined />} color={editing ? 'blue' : 'default'} style={{ marginInlineEnd: 0 }}>
            {editing ? t('collab.office.modeEdit') : t('collab.office.modeView')}
          </Tag>
        )}
        {editing && (
          <Tooltip title={t('collab.office.openViewOnly')}>
            <Button size="small" icon={<EyeOutlined />} onClick={() => navigate(`/files/${id}/edit?view=true`, { replace: true })} />
          </Tooltip>
        )}
        {forceView && f?.access.canEdit && !f.access.workspaceLocked && isOfficeEditable(f.extension) && (
          <Button size="small" type="primary" icon={<EditOutlined />} onClick={() => navigate(`/files/${id}/edit`, { replace: true })}>
            {t('collab.office.switchToEdit')}
          </Button>
        )}
        <Tooltip title={t('common.close')}>
          <Button type="text" icon={<CloseOutlined />} onClick={close} aria-label={t('common.close')} />
        </Tooltip>
      </div>
      <div className="bd-office-body">{body}</div>
    </div>
  )
}
