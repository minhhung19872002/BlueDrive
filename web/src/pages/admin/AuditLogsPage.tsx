import { useMemo, useState } from 'react'
import {
  App,
  Button,
  Card,
  Checkbox,
  Col,
  DatePicker,
  Descriptions,
  Drawer,
  Empty,
  Form,
  Input,
  Row,
  Select,
  Space,
  Table,
  Tag,
  TreeSelect,
  Typography,
} from 'antd'
import { ClearOutlined, EyeOutlined, ReloadOutlined, SafetyCertificateOutlined, SearchOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import type { Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'
import { showApiError } from '@/api/errors'
import { searchAuditLogs, verifyAuditChain } from '@/api/monitoring'
import { qko } from '@/api/queryKeys.ops'
import PageHeader from '@/components/PageHeader'
import ReportExportButtons from '@/features/ops/ReportExportButtons'
import UserLookupSelect from '@/features/ops/UserLookupSelect'
import WorkspaceSelect from '@/features/ops/WorkspaceSelect'
import { prettyJson, toDateParam } from '@/features/ops/utils'
import { useOrgTree } from '@/hooks/useLookups'
import { useCan } from '@/hooks/usePermissions'
import { AUDIT_ACTIONS, RESOURCE_TYPES, type AuditAction, type AuditLog, type AuditLogFilter, type ResourceType } from '@/types/ops'
import { formatDateTime } from '@/utils/format'
import { openPrintView } from '@/utils/print'
import { filterTreeNodeUnaccent, orgTreeToSelect } from '@/utils/tree'

interface FormValues {
  userId?: string
  organizationId?: string
  includeSubOrganizations: boolean
  range?: [Dayjs | null, Dayjs | null] | null
  actions?: AuditAction[]
  resourceType?: ResourceType
  workspaceId?: string
  q?: string
  ip?: string
}

const INITIAL: FormValues = { includeSubOrganizations: true }

/** Colors grouping actions by risk: security events red, destructive orange, sharing purple. */
const ACTION_COLORS: Partial<Record<AuditAction, string>> = {
  login_failed: 'red',
  user_lock: 'red',
  purge: 'red',
  delete: 'volcano',
  permission_change: 'orange',
  config_change: 'orange',
  password_reset: 'orange',
  share: 'purple',
  unshare: 'purple',
  share_link_access: 'purple',
  upload: 'blue',
  download: 'cyan',
  login: 'green',
  logout: 'default',
}

function toFilter(v: FormValues): AuditLogFilter {
  return {
    userId: v.userId,
    organizationId: v.organizationId,
    includeSubOrganizations: v.organizationId ? v.includeSubOrganizations : undefined,
    from: toDateParam(v.range?.[0]),
    to: toDateParam(v.range?.[1]),
    actions: v.actions?.length ? v.actions : undefined,
    resourceType: v.resourceType,
    workspaceId: v.workspaceId,
    q: v.q?.trim() || undefined,
    ip: v.ip?.trim() || undefined,
  }
}

/** HSMT #11/#12: activity log search, detail, integrity check, export (#15) and print. */
export default function AuditLogsPage() {
  const { t } = useTranslation()
  const can = useCan()
  const { modal } = App.useApp()
  const [form] = Form.useForm<FormValues>()
  const [filter, setFilter] = useState<AuditLogFilter>(() => toFilter(INITIAL))
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)
  const [detail, setDetail] = useState<AuditLog | null>(null)
  const [verifying, setVerifying] = useState(false)
  const tree = useOrgTree()
  const treeData = useMemo(() => orgTreeToSelect(tree.data), [tree.data])
  const orgId = Form.useWatch('organizationId', form)

  const query = useQuery({
    queryKey: qko.auditSearch({ filter, page, pageSize }),
    queryFn: () => searchAuditLogs(filter, { page, pageSize }),
    placeholderData: keepPreviousData,
  })

  const actionLabel = (a: AuditAction) => t(`ops.auditAction.${a}`)

  const apply = (values: FormValues) => {
    setFilter(toFilter(values))
    setPage(1)
  }

  const reset = () => {
    form.resetFields()
    apply(INITIAL)
  }

  const verify = async () => {
    setVerifying(true)
    try {
      const r = await verifyAuditChain()
      const content = (
        <Space direction="vertical">
          <span>{r.message}</span>
          <span>{t('ops.audit.verifyChecked', { count: r.checked })}</span>
          {r.firstInvalidId !== null && <span>{t('ops.audit.verifyFirstInvalid', { id: r.firstInvalidId })}</span>}
        </Space>
      )
      if (r.valid) modal.success({ title: t('ops.audit.verifyValid'), content, okText: t('common.close') })
      else modal.error({ title: t('ops.audit.verifyInvalid'), content, okText: t('common.close') })
    } catch (error) {
      showApiError(error)
    } finally {
      setVerifying(false)
    }
  }

  const columns: ColumnsType<AuditLog> = [
    { title: t('ops.audit.time'), dataIndex: 'occurredAt', width: 140, render: formatDateTime },
    {
      title: t('ops.audit.user'),
      key: 'user',
      render: (_, r) =>
        r.userId || r.username ? (
          <Space direction="vertical" size={0}>
            <Typography.Text strong>{r.fullName || r.username}</Typography.Text>
            {r.username && (
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {r.username}
              </Typography.Text>
            )}
          </Space>
        ) : (
          <Typography.Text type="secondary">{t('ops.audit.system')}</Typography.Text>
        ),
    },
    { title: t('ops.audit.organization'), dataIndex: 'organizationName', render: (v: string | null) => v || '' },
    {
      title: t('ops.audit.action'),
      dataIndex: 'action',
      render: (a: AuditAction) => <Tag color={ACTION_COLORS[a]}>{actionLabel(a)}</Tag>,
    },
    {
      title: t('ops.audit.resource'),
      key: 'resource',
      render: (_, r) => (
        <Space size={4} wrap>
          {r.resourceType && <Tag>{t(`ops.resourceType.${r.resourceType}`)}</Tag>}
          <span>{r.resourceName}</span>
        </Space>
      ),
    },
    { title: t('ops.audit.workspace'), dataIndex: 'workspaceName', render: (v: string | null) => v || '' },
    { title: t('ops.audit.ip'), dataIndex: 'ip', render: (v: string | null) => v || '' },
    {
      title: '',
      key: 'detail',
      fixed: 'right',
      width: 56,
      render: (_, r) => (
        <Button type="text" icon={<EyeOutlined />} aria-label={t('ops.audit.detail')} title={t('ops.audit.detail')} onClick={() => setDetail(r)} />
      ),
    },
  ]

  return (
    <>
      <PageHeader
        title={t('menu.auditLogs')}
        description={t('ops.audit.hint')}
        extra={
          <Space wrap>
            {can('audit.admin') && (
              <Button icon={<SafetyCertificateOutlined />} loading={verifying} onClick={() => void verify()}>
                {t('ops.audit.verify')}
              </Button>
            )}
            <ReportExportButtons
              reportType="audit_logs"
              parameters={filter}
              onPrint={() => openPrintView('/print/audit-logs', filter)}
            />
          </Space>
        }
      />
      <Card size="small" style={{ marginBottom: 16 }}>
        <Form<FormValues> form={form} layout="vertical" initialValues={INITIAL} onFinish={apply}>
          <Row gutter={[12, 0]}>
            <Col xs={24} md={12} xl={6}>
              <Form.Item name="userId" label={t('ops.audit.user')}>
                <UserLookupSelect placeholder={t('ops.audit.userPlaceholder')} />
              </Form.Item>
            </Col>
            <Col xs={24} md={12} xl={6}>
              <Form.Item name="organizationId" label={t('ops.audit.organization')} style={{ marginBottom: 4 }}>
                <TreeSelect
                  allowClear
                  showSearch
                  style={{ width: '100%' }}
                  placeholder={t('ops.audit.orgPlaceholder')}
                  treeData={treeData}
                  loading={tree.isLoading}
                  filterTreeNode={filterTreeNodeUnaccent}
                  treeDefaultExpandAll
                  popupMatchSelectWidth={false}
                />
              </Form.Item>
              <Form.Item name="includeSubOrganizations" valuePropName="checked">
                <Checkbox disabled={!orgId}>{t('ops.audit.includeSub')}</Checkbox>
              </Form.Item>
            </Col>
            <Col xs={24} md={12} xl={6}>
              <Form.Item name="range" label={t('ops.audit.dateRange')}>
                <DatePicker.RangePicker style={{ width: '100%' }} format="DD/MM/YYYY" allowEmpty={[true, true]} />
              </Form.Item>
            </Col>
            <Col xs={24} md={12} xl={6}>
              <Form.Item name="actions" label={t('ops.audit.action')}>
                <Select
                  mode="multiple"
                  allowClear
                  maxTagCount="responsive"
                  optionFilterProp="label"
                  placeholder={t('ops.audit.actionPlaceholder')}
                  options={AUDIT_ACTIONS.map((a) => ({ value: a, label: actionLabel(a) }))}
                />
              </Form.Item>
            </Col>
            <Col xs={12} md={6} xl={4}>
              <Form.Item name="resourceType" label={t('ops.audit.resourceType')}>
                <Select
                  allowClear
                  placeholder={t('ops.audit.all')}
                  options={RESOURCE_TYPES.map((r) => ({ value: r, label: t(`ops.resourceType.${r}`) }))}
                />
              </Form.Item>
            </Col>
            <Col xs={12} md={6} xl={5}>
              <Form.Item name="workspaceId" label={t('ops.audit.workspace')}>
                <WorkspaceSelect placeholder={t('ops.audit.all')} />
              </Form.Item>
            </Col>
            <Col xs={24} md={6} xl={6}>
              <Form.Item name="q" label={t('ops.audit.keyword')}>
                <Input allowClear placeholder={t('ops.audit.keywordPlaceholder')} />
              </Form.Item>
            </Col>
            <Col xs={24} md={6} xl={4}>
              <Form.Item name="ip" label={t('ops.audit.ip')}>
                <Input allowClear placeholder="10.0.0." />
              </Form.Item>
            </Col>
            <Col xs={24} xl={5}>
              <Form.Item label=" " className="bd-form-actions">
                <Space wrap>
                  <Button type="primary" htmlType="submit" icon={<SearchOutlined />}>
                    {t('common.search')}
                  </Button>
                  <Button icon={<ClearOutlined />} onClick={reset}>
                    {t('common.clearFilter')}
                  </Button>
                  <Button
                    icon={<ReloadOutlined />}
                    aria-label={t('common.refresh')}
                    title={t('common.refresh')}
                    loading={query.isFetching}
                    onClick={() => void query.refetch()}
                  />
                </Space>
              </Form.Item>
            </Col>
          </Row>
        </Form>
      </Card>
      <Card size="small">
        <Table<AuditLog>
          rowKey="id"
          size="middle"
          columns={columns}
          dataSource={query.data?.items}
          loading={query.isFetching}
          scroll={{ x: 'max-content' }}
          onRow={(r) => ({ onDoubleClick: () => setDetail(r) })}
          pagination={{
            current: page,
            pageSize,
            total: query.data?.total ?? 0,
            showSizeChanger: true,
            pageSizeOptions: [20, 50, 100, 200],
            showTotal: (count) =>
              query.data?.totalCapped
                ? t('common.totalItemsMore', { n: count.toLocaleString('vi-VN') })
                : t('common.totalItems', { count }),
            onChange: (p, s) => {
              setPage(s !== pageSize ? 1 : p)
              setPageSize(s)
            },
          }}
          locale={{ emptyText: <Empty description={t('empty.auditLogs')} /> }}
        />
      </Card>

      <Drawer
        open={!!detail}
        onClose={() => setDetail(null)}
        title={t('ops.audit.detailTitle', { id: detail?.id ?? '' })}
        width={Math.min(640, window.innerWidth)}
      >
        {detail && (
          <Space direction="vertical" size="middle" style={{ width: '100%' }}>
            <Descriptions column={1} size="small" bordered>
              <Descriptions.Item label={t('ops.audit.time')}>{formatDateTime(detail.occurredAt)}</Descriptions.Item>
              <Descriptions.Item label={t('ops.audit.user')}>
                {detail.fullName || detail.username || t('ops.audit.system')}
                {detail.username && detail.fullName ? ` (${detail.username})` : ''}
              </Descriptions.Item>
              <Descriptions.Item label={t('ops.audit.organization')}>{detail.organizationName}</Descriptions.Item>
              <Descriptions.Item label={t('ops.audit.action')}>{actionLabel(detail.action)}</Descriptions.Item>
              <Descriptions.Item label={t('ops.audit.resourceType')}>
                {detail.resourceType ? t(`ops.resourceType.${detail.resourceType}`) : ''}
              </Descriptions.Item>
              <Descriptions.Item label={t('ops.audit.resource')}>{detail.resourceName}</Descriptions.Item>
              <Descriptions.Item label={t('ops.audit.resourceId')}>
                {detail.resourceId && <Typography.Text copyable>{detail.resourceId}</Typography.Text>}
              </Descriptions.Item>
              <Descriptions.Item label={t('ops.audit.workspace')}>{detail.workspaceName}</Descriptions.Item>
              <Descriptions.Item label={t('ops.audit.ip')}>{detail.ip}</Descriptions.Item>
              <Descriptions.Item label={t('ops.audit.userAgent')}>
                <Typography.Text style={{ fontSize: 12, wordBreak: 'break-all' }}>{detail.userAgent}</Typography.Text>
              </Descriptions.Item>
            </Descriptions>
            <div>
              <Typography.Text strong>{t('ops.audit.detailJson')}</Typography.Text>
              {detail.detail ? (
                <pre className="bd-json-block">{prettyJson(detail.detail)}</pre>
              ) : (
                <Typography.Paragraph type="secondary">{t('common.none')}</Typography.Paragraph>
              )}
            </div>
          </Space>
        )}
      </Drawer>
    </>
  )
}
