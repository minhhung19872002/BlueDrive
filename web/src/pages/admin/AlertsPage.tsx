import { useState } from 'react'
import { App, Button, Card, Col, DatePicker, Empty, Row, Select, Space, Table, Tag, Tooltip, Typography } from 'antd'
import { CheckOutlined, ClearOutlined, ReloadOutlined, SafetyOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import type { Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate } from 'react-router-dom'
import { showApiError } from '@/api/errors'
import { acknowledgeAlerts, searchAlerts } from '@/api/monitoring'
import { qko } from '@/api/queryKeys.ops'
import PageHeader from '@/components/PageHeader'
import { CreateIncidentModal } from '@/features/ops/IncidentParts'
import ReportExportButtons from '@/features/ops/ReportExportButtons'
import { incidentDraftFromAlert, toDateParam } from '@/features/ops/utils'
import { useCan } from '@/hooks/usePermissions'
import { ALERT_SEVERITIES, ALERT_TYPES, type AlertDto, type AlertFilter, type AlertSeverity, type CreateIncident } from '@/types/ops'
import { formatDateTime } from '@/utils/format'
import { openPrintView } from '@/utils/print'

const SEVERITY_COLORS: Record<AlertSeverity, string> = { info: 'blue', warning: 'orange', critical: 'red' }

type HandledFilter = 'all' | 'open' | 'handled'

/** HSMT #14: unusual-activity alerts with acknowledge, export (#15) and print. */
export default function AlertsPage() {
  const { t } = useTranslation()
  const can = useCan()
  const { modal, message } = App.useApp()
  const queryClient = useQueryClient()
  const [filter, setFilterState] = useState<AlertFilter>({ acknowledged: false })
  const [range, setRange] = useState<[Dayjs | null, Dayjs | null] | null>(null)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [selected, setSelected] = useState<string[]>([])
  const navigate = useNavigate()
  const [incidentDraft, setIncidentDraft] = useState<CreateIncident | null>(null)
  const canEdit = can('alert.edit')
  const canOpenIncident = can('incident.upload')
  const canViewIncident = can('incident.view')

  const setFilter = (patch: Partial<AlertFilter>) => {
    setFilterState((f) => ({ ...f, ...patch }))
    setPage(1)
    setSelected([])
  }

  const query = useQuery({
    queryKey: qko.alertList({ filter, page, pageSize }),
    queryFn: () => searchAlerts(filter, { page, pageSize }),
    placeholderData: keepPreviousData,
  })

  const handled: HandledFilter = filter.acknowledged === undefined ? 'all' : filter.acknowledged ? 'handled' : 'open'

  const acknowledge = (ids: string[]) =>
    modal.confirm({
      title: t('ops.alert.ackTitle'),
      content: t('ops.alert.ackConfirm', { count: ids.length }),
      okText: t('ops.alert.ack'),
      cancelText: t('common.cancel'),
      onOk: async () => {
        try {
          const r = await acknowledgeAlerts(ids)
          void message.success(t('ops.alert.acked', { count: r.updated }))
          setSelected([])
          await queryClient.invalidateQueries({ queryKey: qko.alerts })
          await queryClient.invalidateQueries({ queryKey: qko.adminDashboard })
        } catch (error) {
          showApiError(error)
        }
      },
    })

  const columns: ColumnsType<AlertDto> = [
    { title: t('ops.alert.time'), dataIndex: 'createdAt', width: 140, render: formatDateTime },
    {
      title: t('ops.alert.severity'),
      dataIndex: 'severity',
      render: (s: AlertSeverity) => <Tag color={SEVERITY_COLORS[s]}>{t(`ops.alertSeverity.${s}`)}</Tag>,
    },
    { title: t('ops.alert.type'), dataIndex: 'type', render: (v: string, r) => r.typeName || t(`ops.alertType.${v}`) },
    {
      title: t('ops.alert.message'),
      dataIndex: 'message',
      render: (v: string, r) => (
        <Space direction="vertical" size={0}>
          <span>{v}</span>
          {(r.relatedUserName || r.relatedWorkspaceName) && (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {[r.relatedUserName && t('ops.alert.relatedUser', { name: r.relatedUserName }), r.relatedWorkspaceName && t('ops.alert.relatedWs', { name: r.relatedWorkspaceName })]
                .filter(Boolean)
                .join(' · ')}
            </Typography.Text>
          )}
        </Space>
      ),
    },
    {
      title: t('common.status'),
      key: 'status',
      render: (_, r) => (
        <Space size={4} wrap>
          {r.isAcknowledged ? (
          <Tooltip title={t('ops.alert.ackedBy', { name: r.acknowledgedByName ?? '', time: formatDateTime(r.acknowledgedAt) })}>
            <Tag color="green">{t('ops.alert.handled')}</Tag>
          </Tooltip>
        ) : (
          <Tag color="red">{t('ops.alert.open')}</Tag>
          )}
          {r.incidentCode &&
            (canViewIncident ? (
              <Link to={`/admin/incidents?id=${r.incidentId}`}>
                <Tag icon={<SafetyOutlined />} color="purple">
                  {r.incidentCode}
                </Tag>
              </Link>
            ) : (
              <Tag icon={<SafetyOutlined />} color="purple">
                {r.incidentCode}
              </Tag>
            ))}
        </Space>
      ),
    },
    ...(canEdit || canOpenIncident
      ? [
          {
            title: t('common.actions'),
            key: 'actions',
            fixed: 'right' as const,
            render: (_: unknown, r: AlertDto) => (
              <Space size={4}>
                {canEdit && !r.isAcknowledged && (
                  <Button size="small" icon={<CheckOutlined />} onClick={() => acknowledge([r.id])}>
                    {t('ops.alert.ackShort')}
                  </Button>
                )}
                {canOpenIncident && !r.incidentId && (
                  <Button
                    size="small"
                    icon={<SafetyOutlined />}
                    onClick={() => setIncidentDraft(incidentDraftFromAlert(r, r.typeName || t(`ops.alertType.${r.type}`)))}
                  >
                    {t('ops.incident.fromAlert')}
                  </Button>
                )}
              </Space>
            ),
          },
        ]
      : []),
  ]

  return (
    <>
      <PageHeader
        title={t('menu.alerts')}
        description={t('ops.alert.hint')}
        extra={
          <Space wrap>
            {canEdit && (
              <Button type="primary" icon={<CheckOutlined />} disabled={selected.length === 0} onClick={() => acknowledge(selected)}>
                {t('ops.alert.ackSelected', { count: selected.length })}
              </Button>
            )}
            <ReportExportButtons reportType="alerts" parameters={filter} onPrint={() => openPrintView('/print/alerts', filter)} />
          </Space>
        }
      />
      <Card size="small">
        <Row gutter={[12, 12]} style={{ marginBottom: 12 }} align="middle">
          <Col xs={24} sm={12} xl={5}>
            <Select
              allowClear
              style={{ width: '100%' }}
              placeholder={t('ops.alert.type')}
              value={filter.type}
              onChange={(v) => setFilter({ type: v })}
              options={ALERT_TYPES.map((a) => ({ value: a, label: t(`ops.alertType.${a}`) }))}
            />
          </Col>
          <Col xs={12} sm={6} xl={4}>
            <Select
              allowClear
              style={{ width: '100%' }}
              placeholder={t('ops.alert.severity')}
              value={filter.severity}
              onChange={(v) => setFilter({ severity: v })}
              options={ALERT_SEVERITIES.map((s) => ({ value: s, label: t(`ops.alertSeverity.${s}`) }))}
            />
          </Col>
          <Col xs={12} sm={6} xl={4}>
            <Select<HandledFilter>
              style={{ width: '100%' }}
              value={handled}
              onChange={(v) => setFilter({ acknowledged: v === 'all' ? undefined : v === 'handled' })}
              options={[
                { value: 'all', label: t('ops.alert.allStatus') },
                { value: 'open', label: t('ops.alert.open') },
                { value: 'handled', label: t('ops.alert.handled') },
              ]}
            />
          </Col>
          <Col xs={24} sm={16} xl={7}>
            <DatePicker.RangePicker
              style={{ width: '100%' }}
              format="DD/MM/YYYY"
              allowEmpty={[true, true]}
              value={range}
              onChange={(v) => {
                setRange(v)
                setFilter({ from: toDateParam(v?.[0]), to: toDateParam(v?.[1]) })
              }}
            />
          </Col>
          <Col xs={24} sm={8} xl={4}>
            <Space>
              <Button
                icon={<ClearOutlined />}
                onClick={() => {
                  setRange(null)
                  setFilterState({ acknowledged: false })
                  setPage(1)
                  setSelected([])
                }}
              >
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
          </Col>
        </Row>
        <Table<AlertDto>
          rowKey="id"
          size="middle"
          columns={columns}
          dataSource={query.data?.items}
          loading={query.isFetching}
          scroll={{ x: 'max-content' }}
          rowSelection={
            canEdit
              ? {
                  selectedRowKeys: selected,
                  onChange: (keys) => setSelected(keys as string[]),
                  getCheckboxProps: (r) => ({ disabled: r.isAcknowledged }),
                }
              : undefined
          }
          pagination={{
            current: page,
            pageSize,
            total: query.data?.total ?? 0,
            showSizeChanger: true,
            pageSizeOptions: [10, 20, 50, 100],
            showTotal: (count) => t('common.totalItems', { count }),
            onChange: (p, s) => {
              setPage(s !== pageSize ? 1 : p)
              setPageSize(s)
            },
          }}
          locale={{ emptyText: <Empty description={t('empty.alerts')} /> }}
        />
      </Card>
      <CreateIncidentModal
        open={!!incidentDraft}
        draft={incidentDraft}
        onClose={() => setIncidentDraft(null)}
        onCreated={(created) => {
          setIncidentDraft(null)
          void queryClient.invalidateQueries({ queryKey: qko.alerts })
          void queryClient.invalidateQueries({ queryKey: qko.incidents })
          if (canViewIncident) navigate(`/admin/incidents?id=${created.id}`)
        }}
      />
    </>
  )
}
