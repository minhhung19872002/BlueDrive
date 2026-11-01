import { useMemo, useState } from 'react'
import { Card, Col, Empty, Input, Row, Select, Table, Tag, Tooltip, Typography } from 'antd'
import { CheckOutlined, LockOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import { useTranslation } from 'react-i18next'
import { qk } from '@/api/queryKeys'
import { exportPermissionComparison, getPermissionComparison, listPermissions, type ComparisonFilter } from '@/api/roles'
import ExportButtons from '@/components/ExportButtons'
import PageHeader from '@/components/PageHeader'
import type { ComparisonRow } from '@/types/admin'

/** HSMT #9: read-only comparison of every function/action against every role. */
export default function PermissionMatrixPage() {
  const { t } = useTranslation()
  const [filter, setFilter] = useState<ComparisonFilter>({})

  const definitions = useQuery({ queryKey: qk.permissions, queryFn: listPermissions, staleTime: 10 * 60_000 })
  const query = useQuery({
    queryKey: [...qk.permissionComparison, filter],
    queryFn: () => getPermissionComparison(filter),
    placeholderData: keepPreviousData,
  })

  const moduleOptions = useMemo(() => {
    const map = new Map<string, string>()
    definitions.data?.forEach((d) => map.set(d.module, d.moduleName))
    return [...map.entries()].map(([value, label]) => ({ value, label }))
  }, [definitions.data])

  const rows = useMemo(() => query.data?.rows ?? [], [query.data])
  // Merge the module and function cells over consecutive rows (rowSpan 0 hides a cell).
  const spans = useMemo(() => {
    const spanOf = (i: number, key: (r: ComparisonRow) => string) => {
      if (i > 0 && key(rows[i - 1]) === key(rows[i])) return 0
      let n = 1
      while (i + n < rows.length && key(rows[i + n]) === key(rows[i])) n++
      return n
    }
    return {
      moduleSpan: rows.map((_, i) => spanOf(i, (r) => r.module)),
      fnSpan: rows.map((_, i) => spanOf(i, (r) => `${r.module}/${r.function}`)),
    }
  }, [rows])

  const roles = query.data?.roles ?? []
  const columns: ColumnsType<ComparisonRow> = [
    {
      title: t('perm.module'),
      dataIndex: 'moduleName',
      fixed: 'left',
      width: 150,
      onCell: (_, i) => ({ rowSpan: spans.moduleSpan[i ?? 0] }),
      render: (v: string) => <Typography.Text strong>{v}</Typography.Text>,
    },
    {
      title: t('perm.function'),
      dataIndex: 'functionName',
      fixed: 'left',
      width: 180,
      onCell: (_, i) => ({ rowSpan: spans.fnSpan[i ?? 0] }),
    },
    {
      title: t('perm.action'),
      dataIndex: 'actionName',
      fixed: 'left',
      width: 150,
      render: (v: string, r) => (
        <Tooltip title={r.code}>
          <span>{v}</span>
        </Tooltip>
      ),
    },
    ...roles.map((role) => ({
      title: (
        <Tooltip title={role.description || role.code}>
          <span>
            {role.isSystem && <LockOutlined style={{ marginRight: 4 }} />}
            {role.name}
          </span>
        </Tooltip>
      ),
      key: role.id,
      align: 'center' as const,
      width: 120,
      render: (_: unknown, r: ComparisonRow) =>
        r.roles[role.id] ? (
          <CheckOutlined style={{ color: '#389e0d' }} aria-label={t('perm.granted')} />
        ) : (
          <Typography.Text type="secondary" aria-label={t('perm.notGranted')}>
            —
          </Typography.Text>
        ),
    })),
  ]

  return (
    <>
      <PageHeader
        title={t('menu.permissionMatrix')}
        description={t('perm.pageHint')}
        extra={<ExportButtons onExport={(format) => exportPermissionComparison(filter, format)} />}
      />
      <Card size="small">
        <Row gutter={[12, 12]} style={{ marginBottom: 12 }}>
          <Col xs={24} md={10} xl={8}>
            <Input.Search
              allowClear
              placeholder={t('perm.searchPlaceholder')}
              onSearch={(q) => setFilter((f) => ({ ...f, q: q || undefined }))}
            />
          </Col>
          <Col xs={24} md={8} xl={6}>
            <Select
              allowClear
              style={{ width: '100%' }}
              placeholder={t('perm.allModules')}
              value={filter.module}
              loading={definitions.isLoading}
              onChange={(module) => setFilter((f) => ({ ...f, module }))}
              options={moduleOptions}
            />
          </Col>
          <Col xs={24} md={6} xl={10} style={{ display: 'flex', alignItems: 'center' }}>
            <Tag>{t('perm.rowCount', { count: rows.length })}</Tag>
            <Tag>{t('perm.roleCount', { count: roles.length })}</Tag>
          </Col>
        </Row>
        <Table<ComparisonRow>
          rowKey="code"
          size="small"
          bordered
          columns={columns}
          dataSource={rows}
          loading={query.isFetching}
          pagination={false}
          scroll={{ x: 480 + roles.length * 120, y: 'calc(100vh - 340px)' }}
          locale={{ emptyText: <Empty description={t('perm.empty')} /> }}
        />
      </Card>
    </>
  )
}
