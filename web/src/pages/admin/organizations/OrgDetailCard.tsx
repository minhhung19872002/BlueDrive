import { Button, Card, Descriptions, Result, Skeleton, Space, Tag } from 'antd'
import { DeleteOutlined, EditOutlined, PlusOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getErrorMessage } from '@/api/errors'
import { getOrganization } from '@/api/organizations'
import { qk } from '@/api/queryKeys'
import { useCan } from '@/hooks/usePermissions'
import type { OrganizationDto } from '@/types/admin'
import { formatDateTime } from '@/utils/format'
import { useDeleteOrganization } from './useDeleteOrganization'

interface OrgDetailCardProps {
  id: string
  onEdit: (org: OrganizationDto) => void
  onAddChild: (parentId: string) => void
  onDeleted: () => void
}

/** Details of the organization selected in the tree, with edit / add child / delete actions. */
export default function OrgDetailCard({ id, onEdit, onAddChild, onDeleted }: OrgDetailCardProps) {
  const { t } = useTranslation()
  const can = useCan()
  const confirmDelete = useDeleteOrganization()
  const { data, isLoading, isError, error } = useQuery({ queryKey: qk.orgDetail(id), queryFn: () => getOrganization(id) })

  if (isLoading) {
    return (
      <Card size="small">
        <Skeleton active />
      </Card>
    )
  }
  if (isError || !data) {
    return (
      <Card size="small">
        <Result status="warning" title={getErrorMessage(error)} />
      </Card>
    )
  }

  const none = t('common.none')
  return (
    <Card
      size="small"
      title={data.name}
      extra={
        <Space wrap>
          {can('org.edit') && (
            <Button size="small" icon={<EditOutlined />} onClick={() => onEdit(data)}>
              {t('common.edit')}
            </Button>
          )}
          {can('org.upload') && (
            <Button size="small" icon={<PlusOutlined />} onClick={() => onAddChild(data.id)}>
              {t('org.addChild')}
            </Button>
          )}
          {can('org.delete') && (
            <Button size="small" danger icon={<DeleteOutlined />} onClick={() => confirmDelete(data, onDeleted)}>
              {t('common.delete')}
            </Button>
          )}
        </Space>
      }
    >
      <Descriptions column={{ xs: 1, sm: 2 }} size="small" bordered>
        <Descriptions.Item label={t('org.code')}>{data.code}</Descriptions.Item>
        <Descriptions.Item label={t('org.typeLabel')}>{t(`org.type.${data.type}`)}</Descriptions.Item>
        <Descriptions.Item label={t('org.parent')} span={2}>
          {data.parentName || t('org.rootLevel')}
        </Descriptions.Item>
        <Descriptions.Item label={t('org.address')} span={2}>
          {data.address || none}
        </Descriptions.Item>
        <Descriptions.Item label={t('org.phone')}>{data.phone || none}</Descriptions.Item>
        <Descriptions.Item label={t('org.email')}>{data.email || none}</Descriptions.Item>
        <Descriptions.Item label={t('org.userCount')}>{data.userCount}</Descriptions.Item>
        <Descriptions.Item label={t('org.childCount')}>{data.childCount}</Descriptions.Item>
        <Descriptions.Item label={t('common.sortOrder')}>{data.sortOrder}</Descriptions.Item>
        <Descriptions.Item label={t('common.status')}>
          {data.isActive ? <Tag color="green">{t('status.active')}</Tag> : <Tag>{t('status.inactive')}</Tag>}
        </Descriptions.Item>
        <Descriptions.Item label={t('common.createdAt')} span={2}>
          {formatDateTime(data.createdAt)}
        </Descriptions.Item>
      </Descriptions>
    </Card>
  )
}
