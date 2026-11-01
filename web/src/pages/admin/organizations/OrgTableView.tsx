import { useMemo } from 'react'
import { Button, Card, Col, Empty, Input, Row, Select, Space, Table, Tag, Tooltip, TreeSelect } from 'antd'
import { ClearOutlined, DeleteOutlined, EditOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import { useTranslation } from 'react-i18next'
import { listOrganizations } from '@/api/organizations'
import { qk } from '@/api/queryKeys'
import { useOrgTree } from '@/hooks/useLookups'
import { useCan } from '@/hooks/usePermissions'
import type { ServerTable } from '@/hooks/useServerTable'
import type { OrganizationDto, OrganizationFilter } from '@/types/admin'
import { filterTreeNodeUnaccent, orgTreeToSelect } from '@/utils/tree'
import { useDeleteOrganization } from './useDeleteOrganization'

interface OrgTableViewProps {
  table: ServerTable<OrganizationFilter>
  onEdit: (org: OrganizationDto) => void
}

export default function OrgTableView({ table, onEdit }: OrgTableViewProps) {
  const { t } = useTranslation()
  const can = useCan()
  const confirmDelete = useDeleteOrganization()
  const tree = useOrgTree()
  const treeData = useMemo(() => orgTreeToSelect(tree.data), [tree.data])
  const { filter, setFilter, resetFilter, params, onTableChange, pagination, sortOrderOf } = table

  const query = useQuery({
    queryKey: [...qk.orgList, params],
    queryFn: () => listOrganizations(params),
    placeholderData: keepPreviousData,
  })

  const columns: ColumnsType<OrganizationDto> = [
    { title: t('org.code'), dataIndex: 'code', key: 'code', sorter: true, sortOrder: sortOrderOf('code'), width: 120 },
    { title: t('org.name'), dataIndex: 'name', key: 'name', sorter: true, sortOrder: sortOrderOf('name') },
    {
      title: t('org.typeLabel'),
      dataIndex: 'type',
      render: (v: OrganizationDto['type']) => <Tag color={v === 'don_vi' ? 'geekblue' : 'blue'}>{t(`org.type.${v}`)}</Tag>,
    },
    { title: t('org.parent'), dataIndex: 'parentName', render: (v: string | null) => v || t('org.rootLevel') },
    { title: t('org.phone'), dataIndex: 'phone' },
    { title: t('org.email'), dataIndex: 'email' },
    { title: t('org.userCount'), dataIndex: 'userCount', align: 'right' },
    {
      title: t('common.status'),
      dataIndex: 'isActive',
      render: (v: boolean) => (v ? <Tag color="green">{t('status.active')}</Tag> : <Tag>{t('status.inactive')}</Tag>),
    },
    {
      title: t('common.actions'),
      key: 'actions',
      fixed: 'right',
      width: 96,
      render: (_, org) => (
        <Space size={4}>
          {can('org.edit') && (
            <Tooltip title={t('common.edit')}>
              <Button size="small" type="text" icon={<EditOutlined />} aria-label={t('common.edit')} onClick={() => onEdit(org)} />
            </Tooltip>
          )}
          {can('org.delete') && (
            <Tooltip title={t('common.delete')}>
              <Button
                size="small"
                type="text"
                danger
                icon={<DeleteOutlined />}
                aria-label={t('common.delete')}
                onClick={() => confirmDelete(org)}
              />
            </Tooltip>
          )}
        </Space>
      ),
    },
  ]

  return (
    <Card size="small">
      <Row gutter={[12, 12]} style={{ marginBottom: 12 }}>
        <Col xs={24} md={12} xl={7}>
          <Input.Search
            allowClear
            defaultValue={filter.q}
            key={`q-${filter.q ?? ''}`}
            placeholder={t('org.searchPlaceholder')}
            onSearch={(q) => setFilter({ q: q || undefined })}
          />
        </Col>
        <Col xs={12} md={6} xl={4}>
          <Select
            allowClear
            style={{ width: '100%' }}
            placeholder={t('common.status')}
            value={filter.isActive === undefined ? undefined : String(filter.isActive)}
            onChange={(v?: string) => setFilter({ isActive: v === undefined ? undefined : v === 'true' })}
            options={[
              { value: 'true', label: t('status.active') },
              { value: 'false', label: t('status.inactive') },
            ]}
          />
        </Col>
        <Col xs={12} md={6} xl={4}>
          <Select
            allowClear
            style={{ width: '100%' }}
            placeholder={t('org.typeLabel')}
            value={filter.type}
            onChange={(v) => setFilter({ type: v })}
            options={[
              { value: 'don_vi', label: t('org.type.don_vi') },
              { value: 'phong_ban', label: t('org.type.phong_ban') },
            ]}
          />
        </Col>
        <Col xs={24} md={18} xl={6}>
          <TreeSelect
            allowClear
            showSearch
            style={{ width: '100%' }}
            placeholder={t('org.parentFilter')}
            treeData={treeData}
            value={filter.parentId}
            onChange={(v?: string) => setFilter({ parentId: v })}
            filterTreeNode={filterTreeNodeUnaccent}
            treeDefaultExpandAll
            popupMatchSelectWidth={false}
          />
        </Col>
        <Col xs={24} md={6} xl={3}>
          <Button icon={<ClearOutlined />} onClick={resetFilter} block>
            {t('common.clearFilter')}
          </Button>
        </Col>
      </Row>
      <Table<OrganizationDto>
        rowKey="id"
        size="middle"
        columns={columns}
        dataSource={query.data?.items}
        loading={query.isFetching}
        pagination={pagination(query.data?.total)}
        onChange={onTableChange}
        scroll={{ x: 'max-content' }}
        locale={{ emptyText: <Empty description={t('empty.organizationsFiltered')} /> }}
      />
    </Card>
  )
}
