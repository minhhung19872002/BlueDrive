import { Form, Input, Modal, Skeleton } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { applyFieldErrors } from '@/api/formErrors'
import { qk } from '@/api/queryKeys'
import { createUserGroup, getUserGroup, updateUserGroup } from '@/api/users'
import { useApiMutation } from '@/hooks/useApiMutation'
import type { SaveUserGroupRequest, UserGroupDetailDto } from '@/types/admin'
import UserPicker from './UserPicker'

interface UserGroupFormModalProps {
  open: boolean
  /** Group id to edit; null to create. */
  editingId: string | null
  onClose: () => void
}

function GroupForm({ detail, onClose }: { detail: UserGroupDetailDto | null; onClose: () => void }) {
  const { t } = useTranslation()
  const [form] = Form.useForm<SaveUserGroupRequest>()
  const mutation = useApiMutation(
    (body: SaveUserGroupRequest) => (detail ? updateUserGroup(detail.id, body) : createUserGroup(body)),
    {
      successMessage: detail ? t('group.updated') : t('group.created'),
      invalidate: [qk.userGroups, qk.users],
      onSuccess: onClose,
      onError: (error) => {
        applyFieldErrors(form, error, ['name', 'description', 'memberIds'])
      },
    },
  )

  return (
    <Modal
      open
      title={detail ? t('group.editTitle') : t('group.createTitle')}
      okText={t('common.save')}
      cancelText={t('common.cancel')}
      confirmLoading={mutation.isPending}
      onOk={() => form.submit()}
      onCancel={onClose}
      width={640}
      destroyOnHidden
    >
      <Form<SaveUserGroupRequest>
        form={form}
        layout="vertical"
        initialValues={{
          name: detail?.name,
          description: detail?.description ?? undefined,
          memberIds: detail?.members.map((m) => m.id) ?? [],
        }}
        onFinish={(v) =>
          mutation.mutate({ name: v.name.trim(), description: v.description?.trim() || null, memberIds: v.memberIds ?? [] })
        }
      >
        <Form.Item
          name="name"
          label={t('group.name')}
          rules={[
            { required: true, whitespace: true, message: t('group.nameRequired') },
            { max: 255, message: t('validation.maxLength', { max: 255 }) },
          ]}
        >
          <Input maxLength={255} autoFocus />
        </Form.Item>
        <Form.Item
          name="description"
          label={t('common.description')}
          rules={[{ max: 2000, message: t('validation.maxLength', { max: 2000 }) }]}
        >
          <Input.TextArea rows={3} maxLength={2000} showCount />
        </Form.Item>
        <Form.Item name="memberIds" label={t('group.members')} extra={t('group.membersHint')}>
          <UserPicker initialUsers={detail?.members} />
        </Form.Item>
      </Form>
    </Modal>
  )
}

function EditLoader({ id, onClose }: { id: string; onClose: () => void }) {
  const { t } = useTranslation()
  const { data, isLoading } = useQuery({ queryKey: qk.userGroup(id), queryFn: () => getUserGroup(id), staleTime: 0 })
  if (isLoading || !data) {
    return (
      <Modal open title={t('group.editTitle')} footer={null} onCancel={onClose}>
        <Skeleton active />
      </Modal>
    )
  }
  return <GroupForm detail={data} onClose={onClose} />
}

export default function UserGroupFormModal({ open, editingId, onClose }: UserGroupFormModalProps) {
  if (!open) return null
  return editingId ? <EditLoader id={editingId} onClose={onClose} /> : <GroupForm detail={null} onClose={onClose} />
}
