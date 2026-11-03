import { useState } from 'react'
import { Button, Empty, Form, Input, List, Popconfirm, Select, Skeleton, Space, Tag, Tooltip, Typography } from 'antd'
import { DeleteOutlined, EditOutlined, ShareAltOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { qk } from '@/api/queryKeys'
import { createShare, deleteShare, listShares } from '@/api/sharing'
import { useApiMutation } from '@/hooks/useApiMutation'
import type { AccessLevel, ResourceType } from '@/types/sharing'
import { formatDateTime } from '@/utils/format'
import AccessDialog from './AccessDialog'
import { LEVEL_COLORS, SHARE_LEVELS, splitPrincipal, type PrincipalValue } from './levels'
import PrincipalIcon from './PrincipalIcon'
import PrincipalSelect from './PrincipalSelect'

interface InternalShareTabProps {
  resourceType: ResourceType
  resourceId: string
  resourceName?: string
  workspaceId?: string
}

interface ShareForm {
  targets: PrincipalValue[]
  accessLevel: AccessLevel
  message?: string
}

/** #49 internal sharing with users, groups and organization units. */
export default function InternalShareTab({ resourceType, resourceId, resourceName, workspaceId }: InternalShareTabProps) {
  const { t } = useTranslation()
  const [editing, setEditing] = useState(false)
  const [form] = Form.useForm<ShareForm>()
  const shares = useQuery({
    queryKey: qk.shares(resourceType, resourceId),
    queryFn: () => listShares(resourceType, resourceId),
  })

  const share = useApiMutation(createShare, {
    successMessage: (r) => t('sharing.shared', { count: r.shared }),
    invalidate: [qk.shares(resourceType, resourceId), qk.access(resourceType, resourceId)],
    onSuccess: () => form.resetFields(),
  })
  const remove = useApiMutation(deleteShare, {
    successMessage: t('sharing.unshared'),
    invalidate: [qk.shares(resourceType, resourceId), qk.access(resourceType, resourceId)],
  })

  return (
    <>
      <Form<ShareForm>
        form={form}
        layout="vertical"
        initialValues={{ accessLevel: 'view', targets: [] }}
        onFinish={(v) =>
          share.mutate({
            resourceType,
            resourceId,
            targets: v.targets.map((p) => splitPrincipal(p.value)),
            accessLevel: v.accessLevel,
            message: v.message?.trim() || null,
          })
        }
      >
        <Form.Item
          name="targets"
          label={t('sharing.targets')}
          extra={t('sharing.targetsHint')}
          rules={[{ required: true, type: 'array', min: 1, message: t('sharing.targetsRequired') }]}
        >
          <PrincipalSelect multiple types={['user', 'group', 'organization']} />
        </Form.Item>
        <Form.Item name="accessLevel" label={t('sharing.level')}>
          <Select options={SHARE_LEVELS.map((l) => ({ value: l, label: t(`sharing.levels.${l}`) }))} />
        </Form.Item>
        <Form.Item name="message" label={t('sharing.message')} rules={[{ max: 1000, message: t('validation.maxLength', { max: 1000 }) }]}>
          <Input.TextArea rows={2} maxLength={1000} placeholder={t('sharing.messagePlaceholder')} />
        </Form.Item>
        <Button type="primary" htmlType="submit" icon={<ShareAltOutlined />} loading={share.isPending}>
          {t('sharing.share')}
        </Button>
      </Form>

      <Typography.Title level={5} style={{ marginTop: 24 }}>
        {t('sharing.currentShares')}
      </Typography.Title>
      {shares.isLoading ? (
        <Skeleton active paragraph={{ rows: 2 }} />
      ) : (
        <List
          size="small"
          dataSource={shares.data ?? []}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('sharing.noShares')} /> }}
          renderItem={(s) => (
            <List.Item
              actions={[
                // Inherited shares can only be removed in "Quản lý quyền truy cập" (it stops inheriting first, like SharePoint).
                ...(s.inheritedFromId
                  ? []
                  : [
                      <Popconfirm
                        key="remove"
                        title={t('sharing.unshareConfirm', { name: s.sharedWithName })}
                        okText={t('common.confirm')}
                        cancelText={t('common.cancel')}
                        onConfirm={() => remove.mutate(s.id)}
                      >
                        <Button type="text" danger size="small" icon={<DeleteOutlined />} aria-label={t('sharing.unshare')} />
                      </Popconfirm>,
                    ]),
                <Tooltip key="edit" title={t('sharing.editAccess')}>
                  <Button type="text" size="small" icon={<EditOutlined />} aria-label={t('sharing.editAccessOf', { name: s.sharedWithName })} onClick={() => setEditing(true)} />
                </Tooltip>,
              ]}
            >
              <List.Item.Meta
                avatar={<PrincipalIcon type={s.sharedWithType} />}
                title={
                  <Space wrap size={4}>
                    <span>{s.sharedWithName}</span>
                    <Tag color={LEVEL_COLORS[s.accessLevel]}>{t(`sharing.levels.${s.accessLevel}`)}</Tag>
                  </Space>
                }
                description={t('sharing.sharedByAt', { name: s.sharedByName, date: formatDateTime(s.createdAt) })}
              />
            </List.Item>
          )}
        />
      )}
      {editing && (
        <AccessDialog
          target={{ type: resourceType, id: resourceId, name: resourceName ?? '', workspaceId: workspaceId ?? '' }}
          onClose={() => {
            setEditing(false)
            void shares.refetch()
          }}
        />
      )}
    </>
  )
}
