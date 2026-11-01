import { useMemo } from 'react'
import { Button, Checkbox, Col, Input, Row, Select, TreeSelect } from 'antd'
import { ClearOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { useAllUserGroups, useOrgTree, useRoles } from '@/hooks/useLookups'
import type { ServerTable } from '@/hooks/useServerTable'
import type { UserFilter, UserStatus } from '@/types/admin'
import { filterTreeNodeUnaccent, orgTreeToSelect } from '@/utils/tree'

const USER_STATUSES: UserStatus[] = ['active', 'locked', 'inactive']

export default function UserFilters({ table }: { table: ServerTable<UserFilter> }) {
  const { t } = useTranslation()
  const { filter, setFilter, resetFilter } = table
  const tree = useOrgTree()
  const roles = useRoles()
  const groups = useAllUserGroups()
  const treeData = useMemo(() => orgTreeToSelect(tree.data), [tree.data])

  return (
    <Row gutter={[12, 12]} style={{ marginBottom: 12 }} align="middle">
      <Col xs={24} md={12} xl={6}>
        <Input.Search
          allowClear
          key={`q-${filter.q ?? ''}`}
          defaultValue={filter.q}
          placeholder={t('user.searchPlaceholder')}
          onSearch={(q) => setFilter({ q: q || undefined })}
        />
      </Col>
      <Col xs={24} md={12} xl={6}>
        <TreeSelect
          allowClear
          showSearch
          style={{ width: '100%' }}
          placeholder={t('user.organization')}
          treeData={treeData}
          loading={tree.isLoading}
          value={filter.organizationId}
          onChange={(v?: string) => setFilter({ organizationId: v })}
          filterTreeNode={filterTreeNodeUnaccent}
          treeDefaultExpandAll
          popupMatchSelectWidth={false}
        />
      </Col>
      <Col xs={24} md={6} xl={3}>
        <Checkbox
          checked={filter.includeSubOrganizations ?? false}
          disabled={!filter.organizationId}
          onChange={(e) => setFilter({ includeSubOrganizations: e.target.checked || undefined })}
        >
          {t('user.includeSubOrgs')}
        </Checkbox>
      </Col>
      <Col xs={12} md={6} xl={3}>
        <Select
          allowClear
          showSearch
          optionFilterProp="label"
          style={{ width: '100%' }}
          placeholder={t('user.role')}
          loading={roles.isLoading}
          value={filter.roleId}
          onChange={(v) => setFilter({ roleId: v })}
          options={roles.data?.map((r) => ({ value: r.id, label: r.name }))}
        />
      </Col>
      <Col xs={12} md={6} xl={3}>
        <Select
          allowClear
          showSearch
          optionFilterProp="label"
          style={{ width: '100%' }}
          placeholder={t('user.group')}
          loading={groups.isLoading}
          value={filter.groupId}
          onChange={(v) => setFilter({ groupId: v })}
          options={groups.data?.map((g) => ({ value: g.id, label: g.name }))}
        />
      </Col>
      <Col xs={12} md={6} xl={2}>
        <Select
          allowClear
          style={{ width: '100%' }}
          placeholder={t('common.status')}
          value={filter.status}
          onChange={(v) => setFilter({ status: v })}
          options={USER_STATUSES.map((s) => ({ value: s, label: t(`user.status.${s}`) }))}
        />
      </Col>
      <Col xs={12} md={6} xl={1}>
        <Button icon={<ClearOutlined />} onClick={resetFilter} aria-label={t('common.clearFilter')} title={t('common.clearFilter')} />
      </Col>
    </Row>
  )
}
