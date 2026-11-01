import { useEffect, useState } from 'react'
import {
  Alert,
  App,
  Button,
  Card,
  Col,
  DatePicker,
  Drawer,
  Empty,
  Form,
  Input,
  Row,
  Select,
  Space,
  Spin,
  Statistic,
  Table,
  Typography,
} from 'antd'
import { ClearOutlined, ReloadOutlined, SaveOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import type { Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { getErrorMessage, showApiError } from '@/api/errors'
import { exportSlaReport, getSlaSummary, getTicket, searchTickets, updateTicket } from '@/api/operations'
import { qko } from '@/api/queryKeys.ops'
import ExportButtons from '@/components/ExportButtons'
import PageHeader from '@/components/PageHeader'
import { TicketDetailView, TicketDue, TicketPriorityTag, TicketStatusTag } from '@/features/ops/TicketParts'
import UserLookupSelect from '@/features/ops/UserLookupSelect'
import { toDateParam } from '@/features/ops/utils'
import { useCan } from '@/hooks/usePermissions'
import {
  TICKET_PRIORITIES,
  TICKET_STATUSES,
  type Ticket,
  type TicketFilter,
  type TicketPriority,
  type TicketStatus,
  type UpdateTicket,
} from '@/types/ops'
import { formatDateTime } from '@/utils/format'

interface UpdateForm {
  assigneeId?: string
  status: TicketStatus
  priority: TicketPriority
  note?: string
  resolution?: string
}

type OverdueFilter = 'all' | 'overdue' | 'ontime'

function ProcessForm({ ticket, onSaved }: { ticket: Ticket; onSaved: (t: Ticket) => void }) {
  const { t } = useTranslation()
  const can = useCan()
  const { message } = App.useApp()
  const [form] = Form.useForm<UpdateForm>()
  const [saving, setSaving] = useState(false)
  const isAdmin = can('ticket.admin')

  useEffect(() => {
    form.setFieldsValue({
      assigneeId: ticket.assigneeId ?? undefined,
      status: ticket.status,
      priority: ticket.priority,
      note: undefined,
      resolution: ticket.resolution ?? undefined,
    })
  }, [form, ticket])

  const save = async (v: UpdateForm) => {
    const body: UpdateTicket = {}
    if (isAdmin && v.assigneeId && v.assigneeId !== ticket.assigneeId) body.assigneeId = v.assigneeId
    if (v.status !== ticket.status) body.status = v.status
    if (v.priority !== ticket.priority) body.priority = v.priority
    if (v.note?.trim()) body.note = v.note.trim()
    if (v.resolution?.trim() && v.resolution.trim() !== (ticket.resolution ?? '')) body.resolution = v.resolution.trim()
    if (Object.keys(body).length === 0) {
      void message.info(t('ops.ticket.noChanges'))
      return
    }
    setSaving(true)
    try {
      const updated = await updateTicket(ticket.id, body)
      void message.success(t('ops.ticket.updated'))
      form.setFieldValue('note', undefined)
      onSaved(updated)
    } catch (error) {
      showApiError(error)
    } finally {
      setSaving(false)
    }
  }

  return (
    <Card size="small" title={t('ops.ticket.processTitle')}>
      <Form<UpdateForm> form={form} layout="vertical" onFinish={(v) => void save(v)} disabled={saving}>
        {isAdmin && (
          <Form.Item name="assigneeId" label={t('ops.ticket.assignee')}>
            <UserLookupSelect placeholder={t('ops.ticket.assigneePlaceholder')} initialLabel={ticket.assigneeName} />
          </Form.Item>
        )}
        <Row gutter={12}>
          <Col xs={12}>
            <Form.Item name="status" label={t('common.status')}>
              <Select options={TICKET_STATUSES.map((s) => ({ value: s, label: t(`ops.ticket.status.${s}`) }))} />
            </Form.Item>
          </Col>
          <Col xs={12}>
            <Form.Item name="priority" label={t('ops.ticket.priorityField')} extra={t('ops.ticket.priorityChangeHint')}>
              <Select options={TICKET_PRIORITIES.map((p) => ({ value: p, label: t(`ops.ticket.priority.${p}`) }))} />
            </Form.Item>
          </Col>
        </Row>
        <Form.Item name="resolution" label={t('ops.ticket.resolution')}>
          <Input.TextArea rows={3} maxLength={4000} showCount placeholder={t('ops.ticket.resolutionPlaceholder')} />
        </Form.Item>
        <Form.Item name="note" label={t('ops.ticket.note')}>
          <Input.TextArea rows={2} maxLength={2000} placeholder={t('ops.ticket.notePlaceholder')} />
        </Form.Item>
        <Button type="primary" htmlType="submit" icon={<SaveOutlined />} loading={saving}>
          {t('ops.ticket.saveUpdate')}
        </Button>
      </Form>
    </Card>
  )
}

/** Support desk administration (quality 5.1): SLA summary, filters, assignment and processing, SLA report. */
export default function TicketsPage() {
  const { t } = useTranslation()
  const can = useCan()
  const queryClient = useQueryClient()
  const [params, setParams] = useSearchParams()
  const openId = params.get('id')
  const [status, setStatus] = useState<TicketStatus | undefined>()
  const [priority, setPriority] = useState<TicketPriority | undefined>()
  const [overdue, setOverdue] = useState<OverdueFilter>('all')
  const [range, setRange] = useState<[Dayjs | null, Dayjs | null] | null>(null)
  const [q, setQ] = useState<string | undefined>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const canProcess = can('ticket.edit') || can('ticket.admin')

  const filter: TicketFilter = {
    status,
    priority,
    overdue: overdue === 'all' ? undefined : overdue === 'overdue',
    from: toDateParam(range?.[0]),
    to: toDateParam(range?.[1]),
  }

  const list = useQuery({
    queryKey: qko.ticketList({ ...filter, q, page, pageSize }),
    queryFn: () => searchTickets(filter, { page, pageSize, q }),
    placeholderData: keepPreviousData,
  })
  const sla = useQuery({
    queryKey: qko.ticketSla({ from: filter.from, to: filter.to }),
    queryFn: () => getSlaSummary(filter.from ?? undefined, filter.to ?? undefined),
  })
  const detail = useQuery({ queryKey: qko.ticket(openId ?? ''), queryFn: () => getTicket(openId!), enabled: !!openId })

  const openTicket = (id: string | null) =>
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
    setPriority(undefined)
    setOverdue('all')
    setRange(null)
    setQ(undefined)
    setPage(1)
  }

  const onSaved = (updated: Ticket) => {
    queryClient.setQueryData(qko.ticket(updated.id), updated)
    void queryClient.invalidateQueries({ queryKey: qko.tickets })
  }

  const s = sla.data
  const columns: ColumnsType<Ticket> = [
    { title: t('ops.ticket.code'), dataIndex: 'code', render: (v: string) => <Typography.Text strong>{v}</Typography.Text> },
    { title: t('ops.ticket.titleField'), dataIndex: 'title' },
    { title: t('ops.ticket.reporter'), dataIndex: 'reporterName' },
    { title: t('ops.ticket.priorityField'), dataIndex: 'priority', render: (v: TicketPriority) => <TicketPriorityTag priority={v} /> },
    { title: t('common.status'), dataIndex: 'status', render: (v: TicketStatus) => <TicketStatusTag status={v} /> },
    {
      title: t('ops.ticket.assignee'),
      dataIndex: 'assigneeName',
      render: (v: string | null) => v || <Typography.Text type="secondary">{t('ops.ticket.unassigned')}</Typography.Text>,
    },
    { title: t('ops.ticket.receivedAt'), dataIndex: 'receivedAt', render: formatDateTime },
    { title: t('ops.ticket.dueAt'), key: 'due', render: (_, r) => <TicketDue ticket={r} /> },
    {
      title: t('ops.ticket.resolvedAt'),
      dataIndex: 'resolvedAt',
      render: (v: string | null, r) =>
        v ? `${formatDateTime(v)}${r.resolutionHours !== null ? ` (${t('ops.ticket.hours', { count: r.resolutionHours })})` : ''}` : '',
    },
  ]

  const fmtHours = (h: number | null | undefined) => (h === null || h === undefined ? '—' : t('ops.ticket.hours', { count: h }))

  return (
    <>
      <PageHeader
        title={t('menu.tickets')}
        description={t('ops.ticket.adminHint')}
        extra={
          <Space wrap>
            <Button
              icon={<ReloadOutlined />}
              loading={list.isFetching || sla.isFetching}
              onClick={() => void queryClient.invalidateQueries({ queryKey: qko.tickets })}
            >
              {t('common.refresh')}
            </Button>
            <ExportButtons onExport={(format) => exportSlaReport(filter, format)} />
          </Space>
        }
      />

      {sla.isError && <Alert type="error" showIcon message={getErrorMessage(sla.error)} style={{ marginBottom: 16 }} />}
      <Row gutter={[16, 16]} style={{ marginBottom: 16 }}>
        {[
          { key: 'total', title: t('ops.ticket.sla.total'), value: s?.total ?? 0 },
          { key: 'open', title: t('ops.ticket.sla.open'), value: s?.open ?? 0 },
          { key: 'resolved', title: t('ops.ticket.sla.resolved'), value: s?.resolved ?? 0 },
          { key: 'overdue', title: t('ops.ticket.sla.overdue'), value: s?.overdue ?? 0, color: (s?.overdue ?? 0) > 0 ? '#cf1322' : undefined },
          {
            key: 'ontime',
            title: t('ops.ticket.sla.onTime'),
            value: `${(s?.onTimePercent ?? 0).toLocaleString('vi-VN')}%`,
            color: (s?.onTimePercent ?? 100) < 90 ? '#d46b08' : '#389e0d',
          },
          { key: 'urgent', title: t('ops.ticket.sla.avgUrgent'), value: fmtHours(s?.avgHoursUrgent) },
          { key: 'normal', title: t('ops.ticket.sla.avgNormal'), value: fmtHours(s?.avgHoursNormal) },
        ].map((c) => (
          <Col key={c.key} xs={12} md={8} xl={c.key === 'urgent' || c.key === 'normal' ? 4 : 3}>
            <Card size="small" loading={sla.isLoading} style={{ height: '100%' }}>
              <Statistic title={c.title} value={c.value} valueStyle={c.color ? { color: c.color } : undefined} />
            </Card>
          </Col>
        ))}
      </Row>
      <Typography.Paragraph type="secondary" style={{ fontSize: 12 }}>
        {t('ops.ticket.slaRule')}
      </Typography.Paragraph>

      <Card size="small">
        <Row gutter={[12, 12]} style={{ marginBottom: 12 }} align="middle">
          <Col xs={24} md={12} xl={5}>
            <Input.Search
              allowClear
              placeholder={t('ops.ticket.searchPlaceholder')}
              onSearch={(v) => {
                setQ(v.trim() || undefined)
                resetPage()
              }}
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
              options={TICKET_STATUSES.map((x) => ({ value: x, label: t(`ops.ticket.status.${x}`) }))}
            />
          </Col>
          <Col xs={12} md={6} xl={3}>
            <Select
              allowClear
              style={{ width: '100%' }}
              placeholder={t('ops.ticket.priorityField')}
              value={priority}
              onChange={(v) => {
                setPriority(v)
                resetPage()
              }}
              options={TICKET_PRIORITIES.map((x) => ({ value: x, label: t(`ops.ticket.priority.${x}`) }))}
            />
          </Col>
          <Col xs={12} md={6} xl={3}>
            <Select<OverdueFilter>
              style={{ width: '100%' }}
              value={overdue}
              onChange={(v) => {
                setOverdue(v)
                resetPage()
              }}
              options={[
                { value: 'all', label: t('ops.ticket.slaAll') },
                { value: 'overdue', label: t('ops.ticket.overdue') },
                { value: 'ontime', label: t('ops.ticket.onTime') },
              ]}
            />
          </Col>
          <Col xs={24} md={12} xl={6}>
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
          <Col xs={12} md={6} xl={3}>
            <Button icon={<ClearOutlined />} onClick={clear}>
              {t('common.clearFilter')}
            </Button>
          </Col>
        </Row>
        <Table<Ticket>
          rowKey="id"
          size="middle"
          columns={columns}
          dataSource={list.data?.items}
          loading={list.isFetching}
          scroll={{ x: 'max-content' }}
          rowClassName={(r) => (r.isOverdue ? 'bd-row-overdue' : '')}
          onRow={(r) => ({ onClick: () => openTicket(r.id), style: { cursor: 'pointer' } })}
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
          locale={{ emptyText: <Empty description={t('empty.tickets')} /> }}
        />
      </Card>

      <Drawer
        open={!!openId}
        onClose={() => openTicket(null)}
        width={Math.min(720, window.innerWidth)}
        title={detail.data ? `${detail.data.code} — ${detail.data.title}` : t('ops.ticket.detail')}
      >
        {detail.isLoading ? (
          <div style={{ textAlign: 'center', padding: 48 }}>
            <Spin />
          </div>
        ) : detail.isError ? (
          <Alert type="error" showIcon message={getErrorMessage(detail.error)} />
        ) : detail.data ? (
          <Space direction="vertical" size="middle" style={{ width: '100%' }}>
            {canProcess && <ProcessForm ticket={detail.data} onSaved={onSaved} />}
            <TicketDetailView ticket={detail.data} />
          </Space>
        ) : null}
      </Drawer>
    </>
  )
}
