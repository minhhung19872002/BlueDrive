import { useEffect, useState } from 'react'
import { Alert, App, Button, Card, Col, DatePicker, Drawer, Empty, Form, Input, Row, Select, Space, Spin, Table, Typography } from 'antd'
import { ClearOutlined, PlusOutlined, ReloadOutlined, SaveOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import type { Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { getErrorMessage, showApiError } from '@/api/errors'
import { exportIncident, exportIncidents, getIncident, searchIncidents, updateIncident } from '@/api/operations'
import { qko } from '@/api/queryKeys.ops'
import ExportButtons from '@/components/ExportButtons'
import PageHeader from '@/components/PageHeader'
import { CreateIncidentModal, IncidentDetailView, IncidentSeverityTag, IncidentStatusTag } from '@/features/ops/IncidentParts'
import UserLookupSelect from '@/features/ops/UserLookupSelect'
import { toDateParam } from '@/features/ops/utils'
import { useCan } from '@/hooks/usePermissions'
import {
  INCIDENT_CATEGORIES,
  INCIDENT_SEVERITIES,
  INCIDENT_STATUSES,
  type Incident,
  type IncidentCategory,
  type IncidentFilter,
  type IncidentSeverity,
  type IncidentStatus,
  type UpdateIncident,
} from '@/types/ops'
import { formatDateTime } from '@/utils/format'

interface ProcessValues {
  status: IncidentStatus
  handlerId?: string
  category: IncidentCategory
  severity: IncidentSeverity
  affectedSystems?: string
  cause?: string
  coordinatingParties?: string
  remediation?: string
  conclusion?: string
  note?: string
}

type TextField = 'affectedSystems' | 'cause' | 'coordinatingParties' | 'remediation' | 'conclusion'
const TEXT_FIELDS: TextField[] = ['affectedSystems', 'cause', 'coordinatingParties', 'remediation', 'conclusion']

type OpenFilter = 'all' | 'open' | 'done'

function ProcessForm({ incident, onSaved }: { incident: Incident; onSaved: (i: Incident) => void }) {
  const { t } = useTranslation()
  const can = useCan()
  const { message } = App.useApp()
  const [form] = Form.useForm<ProcessValues>()
  const [saving, setSaving] = useState(false)
  const isAdmin = can('incident.admin')
  const closed = incident.status === 'closed'
  const locked = closed && !isAdmin

  useEffect(() => {
    form.setFieldsValue({
      status: incident.status,
      handlerId: incident.handlerId ?? undefined,
      category: incident.category,
      severity: incident.severity,
      affectedSystems: incident.affectedSystems ?? undefined,
      cause: incident.cause ?? undefined,
      coordinatingParties: incident.coordinatingParties ?? undefined,
      remediation: incident.remediation ?? undefined,
      conclusion: incident.conclusion ?? undefined,
      note: undefined,
    })
  }, [form, incident])

  const save = async (v: ProcessValues) => {
    const body: UpdateIncident = {}
    if (v.status !== incident.status) body.status = v.status
    if (isAdmin && v.handlerId && v.handlerId !== incident.handlerId) body.handlerId = v.handlerId
    if (v.category !== incident.category) body.category = v.category
    if (v.severity !== incident.severity) body.severity = v.severity
    for (const f of TEXT_FIELDS) {
      const value = (v[f] ?? '').trim()
      if (value !== (incident[f] ?? '')) body[f] = value
    }
    if (v.note?.trim()) body.note = v.note.trim()
    if (Object.keys(body).length === 0) {
      void message.info(t('ops.incident.noChanges'))
      return
    }
    setSaving(true)
    try {
      const updated = await updateIncident(incident.id, body)
      void message.success(t('ops.incident.updated'))
      form.setFieldValue('note', undefined)
      onSaved(updated)
    } catch (error) {
      showApiError(error)
    } finally {
      setSaving(false)
    }
  }

  const statuses = INCIDENT_STATUSES.filter((s) => isAdmin || s !== 'closed' || closed)

  return (
    <Card size="small" title={t('ops.incident.processTitle')}>
      {closed && (
        <Alert type="info" showIcon style={{ marginBottom: 12 }} message={isAdmin ? t('ops.incident.closedAdminHint') : t('ops.incident.closedHint')} />
      )}
      <Form<ProcessValues> form={form} layout="vertical" onFinish={(v) => void save(v)} disabled={saving || locked}>
        <Row gutter={12}>
          <Col xs={24} sm={12}>
            <Form.Item name="status" label={t('common.status')} extra={t('ops.incident.statusHint')}>
              <Select options={statuses.map((s) => ({ value: s, label: t(`ops.incident.status.${s}`) }))} />
            </Form.Item>
          </Col>
          {isAdmin && (
            <Col xs={24} sm={12}>
              <Form.Item name="handlerId" label={t('ops.incident.handler')}>
                <UserLookupSelect placeholder={t('ops.incident.handlerPlaceholder')} initialLabel={incident.handlerName} />
              </Form.Item>
            </Col>
          )}
          <Col xs={24} sm={12}>
            <Form.Item name="category" label={t('ops.incident.categoryField')}>
              <Select options={INCIDENT_CATEGORIES.map((c) => ({ value: c, label: t(`ops.incident.category.${c}`) }))} />
            </Form.Item>
          </Col>
          <Col xs={24} sm={12}>
            <Form.Item name="severity" label={t('ops.incident.severityField')}>
              <Select options={INCIDENT_SEVERITIES.map((s) => ({ value: s, label: t(`ops.incident.severity.${s}`) }))} />
            </Form.Item>
          </Col>
        </Row>
        <Form.Item name="affectedSystems" label={t('ops.incident.affectedSystems')}>
          <Input.TextArea rows={2} maxLength={2000} />
        </Form.Item>
        <Form.Item name="cause" label={t('ops.incident.cause')}>
          <Input.TextArea rows={3} maxLength={10000} />
        </Form.Item>
        <Form.Item name="coordinatingParties" label={t('ops.incident.coordinatingParties')} extra={t('ops.incident.coordinatingHint')}>
          <Input.TextArea rows={2} maxLength={2000} />
        </Form.Item>
        <Form.Item name="remediation" label={t('ops.incident.remediation')}>
          <Input.TextArea rows={3} maxLength={10000} />
        </Form.Item>
        <Form.Item name="conclusion" label={t('ops.incident.conclusion')}>
          <Input.TextArea rows={3} maxLength={10000} />
        </Form.Item>
        <Form.Item name="note" label={t('ops.incident.note')}>
          <Input.TextArea rows={2} maxLength={4000} placeholder={t('ops.incident.notePlaceholder')} />
        </Form.Item>
        <Button type="primary" htmlType="submit" icon={<SaveOutlined />} loading={saving}>
          {t('ops.incident.saveUpdate')}
        </Button>
      </Form>
    </Card>
  )
}

/** HSMT 3.4: security incident records — register, handling workflow, timeline and PDF/Excel export. */
export default function IncidentsPage() {
  const { t } = useTranslation()
  const can = useCan()
  const queryClient = useQueryClient()
  const [params, setParams] = useSearchParams()
  const openId = params.get('id')
  const [status, setStatus] = useState<IncidentStatus | undefined>()
  const [severity, setSeverity] = useState<IncidentSeverity | undefined>()
  const [category, setCategory] = useState<IncidentCategory | undefined>()
  const [openFilter, setOpenFilter] = useState<OpenFilter>('all')
  const [range, setRange] = useState<[Dayjs | null, Dayjs | null] | null>(null)
  const [q, setQ] = useState<string | undefined>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [creating, setCreating] = useState(false)
  const canProcess = can('incident.edit') || can('incident.admin')

  const filter: IncidentFilter = {
    status,
    severity,
    category,
    open: openFilter === 'all' ? undefined : openFilter === 'open',
    from: toDateParam(range?.[0]),
    to: toDateParam(range?.[1]),
  }

  const list = useQuery({
    queryKey: qko.incidentList({ ...filter, q, page, pageSize }),
    queryFn: () => searchIncidents(filter, { page, pageSize, q }),
    placeholderData: keepPreviousData,
  })
  const detail = useQuery({ queryKey: qko.incident(openId ?? ''), queryFn: () => getIncident(openId!), enabled: !!openId })

  const openIncident = (id: string | null) =>
    setParams(
      (prev) => {
        const next = new URLSearchParams(prev)
        if (id) next.set('id', id)
        else next.delete('id')
        return next
      },
      { replace: true },
    )

  const resetPage = () => setPage(1)

  const clear = () => {
    setStatus(undefined)
    setSeverity(undefined)
    setCategory(undefined)
    setOpenFilter('all')
    setRange(null)
    setQ(undefined)
    setPage(1)
  }

  const onSaved = (updated: Incident) => {
    queryClient.setQueryData(qko.incident(updated.id), updated)
    void queryClient.invalidateQueries({ queryKey: qko.incidents })
  }

  const columns: ColumnsType<Incident> = [
    { title: t('ops.incident.code'), dataIndex: 'code', render: (v: string) => <Typography.Text strong>{v}</Typography.Text> },
    { title: t('ops.incident.titleField'), dataIndex: 'title' },
    { title: t('ops.incident.categoryField'), dataIndex: 'category', render: (v: IncidentCategory) => t(`ops.incident.category.${v}`) },
    { title: t('ops.incident.severityField'), dataIndex: 'severity', render: (v: IncidentSeverity) => <IncidentSeverityTag severity={v} /> },
    { title: t('common.status'), dataIndex: 'status', render: (v: IncidentStatus) => <IncidentStatusTag status={v} /> },
    {
      title: t('ops.incident.handler'),
      dataIndex: 'handlerName',
      render: (v: string | null) => v || <Typography.Text type="secondary">{t('ops.incident.unassigned')}</Typography.Text>,
    },
    { title: t('ops.incident.detectedAt'), dataIndex: 'detectedAt', render: formatDateTime },
    { title: t('ops.incident.closedAt'), dataIndex: 'closedAt', render: (v: string | null) => (v ? formatDateTime(v) : '') },
  ]

  return (
    <>
      <PageHeader
        title={t('menu.incidents')}
        description={t('ops.incident.hint')}
        extra={
          <Space wrap>
            <Button icon={<ReloadOutlined />} loading={list.isFetching} onClick={() => void queryClient.invalidateQueries({ queryKey: qko.incidents })}>
              {t('common.refresh')}
            </Button>
            <ExportButtons onExport={(format) => exportIncidents(filter, format)} />
            {can('incident.upload') && (
              <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreating(true)}>
                {t('ops.incident.create')}
              </Button>
            )}
          </Space>
        }
      />

      <Card size="small">
        <Row gutter={[12, 12]} style={{ marginBottom: 12 }} align="middle">
          <Col xs={24} md={12} xl={8}>
            <Input.Search
              allowClear
              placeholder={t('ops.incident.searchPlaceholder')}
              onSearch={(v) => {
                setQ(v.trim() || undefined)
                resetPage()
              }}
            />
          </Col>
          <Col xs={12} md={6} xl={4}>
            <Select<OpenFilter>
              style={{ width: '100%' }}
              value={openFilter}
              onChange={(v) => {
                setOpenFilter(v)
                resetPage()
              }}
              options={[
                { value: 'all', label: t('ops.incident.allRecords') },
                { value: 'open', label: t('ops.incident.openRecords') },
                { value: 'done', label: t('ops.incident.doneRecords') },
              ]}
            />
          </Col>
          <Col xs={12} md={6} xl={4}>
            <Select
              allowClear
              style={{ width: '100%' }}
              placeholder={t('common.status')}
              value={status}
              onChange={(v) => {
                setStatus(v)
                resetPage()
              }}
              options={INCIDENT_STATUSES.map((x) => ({ value: x, label: t(`ops.incident.status.${x}`) }))}
            />
          </Col>
          <Col xs={12} md={6} xl={4}>
            <Select
              allowClear
              style={{ width: '100%' }}
              placeholder={t('ops.incident.severityField')}
              value={severity}
              onChange={(v) => {
                setSeverity(v)
                resetPage()
              }}
              options={INCIDENT_SEVERITIES.map((x) => ({ value: x, label: t(`ops.incident.severity.${x}`) }))}
            />
          </Col>
          <Col xs={12} md={6} xl={4}>
            <Select
              allowClear
              style={{ width: '100%' }}
              placeholder={t('ops.incident.categoryField')}
              value={category}
              onChange={(v) => {
                setCategory(v)
                resetPage()
              }}
              options={INCIDENT_CATEGORIES.map((x) => ({ value: x, label: t(`ops.incident.category.${x}`) }))}
            />
          </Col>
          <Col xs={24} md={12} xl={8}>
            <DatePicker.RangePicker
              style={{ width: '100%' }}
              format="DD/MM/YYYY"
              allowEmpty={[true, true]}
              value={range}
              onChange={(v) => {
                setRange(v)
                resetPage()
              }}
            />
          </Col>
          <Col flex="none">
            <Button icon={<ClearOutlined />} onClick={clear}>
              {t('common.clearFilter')}
            </Button>
          </Col>
        </Row>
        <Table<Incident>
          rowKey="id"
          size="middle"
          columns={columns}
          dataSource={list.data?.items}
          loading={list.isFetching}
          scroll={{ x: 'max-content' }}
          onRow={(r) => ({ onClick: () => openIncident(r.id), style: { cursor: 'pointer' } })}
          pagination={{
            current: page,
            pageSize,
            total: list.data?.total ?? 0,
            showSizeChanger: true,
            pageSizeOptions: [10, 20, 50, 100],
            showTotal: (count) => t('common.totalItems', { count }),
            onChange: (p, size) => {
              setPage(size !== pageSize ? 1 : p)
              setPageSize(size)
            },
          }}
          locale={{ emptyText: <Empty description={t('ops.incident.empty')} /> }}
        />
      </Card>

      <Drawer
        open={!!openId}
        onClose={() => openIncident(null)}
        width={Math.min(760, window.innerWidth)}
        title={detail.data ? `${detail.data.code} — ${detail.data.title}` : t('ops.incident.detail')}
        extra={detail.data ? <ExportButtons onExport={(format) => exportIncident(detail.data.id, format)} /> : null}
      >
        {detail.isLoading ? (
          <div style={{ textAlign: 'center', padding: 48 }}>
            <Spin />
          </div>
        ) : detail.isError ? (
          <Alert type="error" showIcon message={getErrorMessage(detail.error)} />
        ) : detail.data ? (
          <Space direction="vertical" size="middle" style={{ width: '100%' }}>
            {canProcess && <ProcessForm incident={detail.data} onSaved={onSaved} />}
            <IncidentDetailView incident={detail.data} />
          </Space>
        ) : null}
      </Drawer>

      <CreateIncidentModal
        open={creating}
        onClose={() => setCreating(false)}
        onCreated={(created) => {
          setCreating(false)
          setPage(1)
          void queryClient.invalidateQueries({ queryKey: qko.incidents })
          openIncident(created.id)
        }}
      />
    </>
  )
}
