import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { createDocument } from '@/api/office'
import { qk } from '@/api/queryKeys'
import NameModal from '@/features/drive/dialogs/NameModal'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useOfficeNew } from '@/stores/officeNew'
import { editorUrl } from './officeFiles'

/** Global "Mới → Tài liệu Word / Bảng tính / Trình chiếu / Văn bản" dialog (#21, #33): asks the name, creates, opens the editor. */
export default function OfficeNewHost() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const request = useOfficeNew((s) => s.request)
  const close = useOfficeNew((s) => s.close)
  const create = useApiMutation(
    (name: string) =>
      createDocument({
        workspaceId: request!.target.workspaceId,
        folderId: request!.target.folderId,
        kind: request!.kind,
        name: name || null,
      }),
    {
      successMessage: (r) => t('collab.newDoc.created', { name: r.name }),
      invalidate: [qk.drive, qk.workspaces],
      onSuccess: (r) => {
        close()
        navigate(editorUrl(r.fileId))
      },
    },
  )
  if (!request) return null
  return (
    <NameModal
      key={`${request.kind}:${request.target.workspaceId}:${request.target.folderId ?? 'root'}`}
      title={t(`collab.newDoc.title.${request.kind}`, { name: request.targetName })}
      label={t('collab.newDoc.nameLabel', { ext: request.kind })}
      initialName={t(`collab.newDoc.defaultName.${request.kind}`)}
      okText={t('collab.newDoc.createAndOpen')}
      loading={create.isPending}
      onSubmit={(name) => create.mutateAsync(name.replace(new RegExp(`\\.${request.kind}$`, 'i'), ''))}
      onClose={close}
    />
  )
}
