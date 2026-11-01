import { useState } from 'react'
import {
  Alert,
  App,
  Button,
  Card,
  Col,
  Drawer,
  Empty,
  Form,
  Input,
  Radio,
  Row,
  Space,
  Spin,
  Table,
  Typography,
  Upload,
  type UploadFile,
} from 'antd'
import { CloseCircleOutlined, PaperClipOutlined, ReloadOutlined, SendOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { getErrorMessage, showApiError } from '@/api/errors'
import { createTicket, getTicket, searchTickets, updateTicket, uploadAttachment } from '@/api/operations'
import { qko } from '@/api/queryKeys.ops'
import PageHeader from '@/components/PageHeader'
import { TicketDetailView, TicketDue, TicketPriorityTag, TicketStatusTag } from '@/features/ops/TicketParts'
import '@/features/ops/ops.css'
import { useCan } from '@/hooks/usePermissions'
import { useAuthStore } from '@/stores/auth'
import type { Ticket, TicketPriority } from '@/types/ops'
import { formatDateTime } from '@/utils/format'

interface CreateForm {
  title: string
  content: string
  priority: TicketPriority
}

const MAX_ATTACHMENTS = 5
const MAX_ATTACHMENT_BYTES = 50 * 1024 * 1024

function TicketDrawer({ id, onClose }: { id: string | null; onClose: () => void }) {
  const { t } = useTranslation()
  const { message, modal } = App.useApp()
  const queryClient = useQueryClient()
  const [note, setNote] = useState('')
  const [saving, setSaving] = useState(false)
  const detail = useQuery({ queryKey: qko.ticket(id ?? ''), queryFn: () => getTicket(id!), enabled: !!id })
  const ticket = detail.data
  const closed = ticket?.status === 'dong'

  const send = async (body: Parameters<typeof updateTicket>[1], success: string) => {
    if (!id) return
    setSaving(true)
    try {
      const updated = await updateTicket(id, body)
      queryClient.setQueryData(qko.ticket(id), updated)
      void message.success(success)
      setNote('')
      await queryClient.invalidateQueries({ queryKey: qko.tickets })
    } catch (error) {
      showApiError(error)
    } finally {
      setSaving(false)
    }
  }

  const closeTicket = () =>
    modal.confirm({
      title: t('ops.ticket.closeTitle'),
      content: t('ops.ticket.closeConfirm'),
      okText: t('ops.ticket.closeTicket'),
      cancelText: t('common.cancel'),
      onOk: () => send({ status: 'dong', note: note.trim() || undefined }, t('ops.ticket.closed')),
    })

  return (
    <Drawer
      open={!!id}
      onClose={onClose}
      width={Math.min(640, window.innerWidth)}
      title={ticket ? `${ticket.code} — ${ticket.title}` : t('ops.ticket.detail')}
      extra={
        ticket &&
        !closed && (
          <Button danger icon={<CloseCircleOutlined />} loading={saving} onClick={closeTicket}>
            {t('ops.ticket.closeTicket')}
          </Button>
        )
      }
    >
      {detail.isLoading ? (
        <div style={{ textAlign: 'center', padding: 48 }}>
          <Spin />
        </div>
      ) : detail.isError ? (
        <Alert type="error" showIcon message={getErrorMessage(detail.error)} />
      ) : ticket ? (
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <TicketDetailView ticket={ticket} />
          {!closed && (
            <Card size="small" title={t('ops.ticket.addNote')}>
              <Input.TextArea rows={3} maxLength={2000} showCount value={note} onChange={(e) => setNote(e.target.value)} />
              <Button
                type="primary"
                icon={<SendOutlined />}
                style={{ marginTop: 8 }}
                disabled={!note.trim()}
                loading={saving}
                onClick={() => void send({ note: note.trim() }, t('ops.ticket.noteAdded'))}
              >
                {t('ops.ticket.sendNote')}
              </Button>
            </Card>
          )}
        </Space>
      ) : null}
    </Drawer>
  )
}

/** Support desk for end users (quality 5.1): submit a request/incident and follow its processing. */
export default function SupportPage() {
  const { t } = useTranslation()
  const can = useCan()
  const { message } = App.useApp()
  const queryClient = useQueryClient()
  const workspaceId = useAuthStore((s) => s.user?.personalWorkspaceId ?? null)
  const [form] = Form.useForm<CreateForm>()
  const [files, setFiles] = useState<UploadFile[]>([])
  const [submitting, setSubmitting] = useState(false)
  const [page, setPage] = useState(1)
  const [params, setParams] = useSearchParams()
  const openId = params.get('id')
  const canCreate = can('ticket.upload')

  const list = useQuery({
    queryKey: qko.ticketList({ mine: true, page }),
    queryFn: () => searchTickets({ mine: true }, { page, pageSize: 10 }),
    placeholderData: keepPreviousData,
  })

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

  const submit = async (v: CreateForm) => {
    setSubmitting(true)
    try {
      const attachmentFileIds: string[] = []
      if (files.length > 0) {
        if (!workspaceId) throw new Error(t('ops.ticket.noPersonalWorkspace'))
        for (const f of files) {
          if (!f.originFileObj) continue
          const res = await uploadAttachment(f.originFileObj, workspaceId)
          attachmentFileIds.push(res.fileId)
        }
      }
      const created = await createTicket({
        title: v.title.trim(),
        content: v.content.trim(),
        priority: v.priority,
        attachmentFileIds: attachmentFileIds.length ? attachmentFileIds : undefined,
      })
      void message.success(t('ops.ticket.created', { code: created.code, due: formatDateTime(created.dueAt) }))
      form.resetFields()
      setFiles([])
      setPage(1)
      await queryClient.invalidateQueries({ queryKey: qko.tickets })
      openTicket(created.id)
    } catch (error) {
      showApiError(error)
    } finally {
      setSubmitting(false)
    }
  }

  const columns: ColumnsType<Ticket> = [
    { title: t('ops.ticket.code'), dataIndex: 'code', render: (v: string) => <Typography.Text strong>{v}</Typography.Text> },
    { title: t('ops.ticket.titleField'), dataIndex: 'title' },
    { title: t('ops.ticket.priorityField'), dataIndex: 'priority', render: (v: TicketPriority) => <TicketPriorityTag priority={v} /> },
    { title: t('common.status'), dataIndex: 'status', render: (v: Ticket['status']) => <TicketStatusTag status={v} /> },
    { title: t('ops.ticket.receivedAt'), dataIndex: 'receivedAt', render: formatDateTime },
    { title: t('ops.ticket.dueAt'), key: 'due', render: (_, r) => <TicketDue ticket={r} /> },
  ]

  return (
    <>
      <PageHeader title={t('header.support')} description={t('ops.ticket.userHint')} />
      <Row gutter={[16, 16]}>
        <Col xs={24} xl={10}>
          <Card title={t('ops.ticket.createTitle')}>
            {!canCreate ? (
              <Alert type="warning" showIcon message={t('ops.ticket.noCreatePermission')} />
            ) : (
              <Form<CreateForm>
                form={form}
                layout="vertical"
                initialValues={{ priority: 'khong_khan_cap' }}
                onFinish={(v) => void submit(v)}
                disabled={submitting}
              >
                <Form.Item
                  name="title"
                  label={t('ops.ticket.titleField')}
                  rules={[{ required: true, whitespace: true, message: t('ops.ticket.titleRequired') }]}
                >
                  <Input maxLength={300} placeholder={t('ops.ticket.titlePlaceholder')} />
                </Form.Item>
                <Form.Item
                  name="content"
                  label={t('ops.ticket.content')}
                  rules={[{ required: true, whitespace: true, message: t('ops.ticket.contentRequired') }]}
                >
                  <Input.TextArea rows={5} maxLength={10000} showCount placeholder={t('ops.ticket.contentPlaceholder')} />
                </Form.Item>
                <Form.Item name="priority" label={t('ops.ticket.priorityField')}>
                  <Radio.Group>
                    <Space direction="vertical">
                      <Radio value="khan_cap">{t('ops.ticket.priorityUrgentHint')}</Radio>
                      <Radio value="khong_khan_cap">{t('ops.ticket.priorityNormalHint')}</Radio>
                    </Space>
                  </Radio.Group>
                </Form.Item>
                <Form.Item label={t('ops.ticket.attachments')} extra={t('ops.ticket.attachmentsHint', { max: MAX_ATTACHMENTS })}>
                  <Upload
                    multiple
                    fileList={files}
                    disabled={!workspaceId || submitting}
                    beforeUpload={(file) => {
                      if (file.size > MAX_ATTACHMENT_BYTES) {
                        void message.error(t('ops.ticket.attachmentTooLarge', { name: file.name }))
                        return Upload.LIST_IGNORE
                      }
                      return false
                    }}
                    onChange={({ fileList }) => setFiles(fileList.slice(0, MAX_ATTACHMENTS))}
                  >
                    <Button icon={<PaperClipOutlined />} disabled={!workspaceId || files.length >= MAX_ATTACHMENTS}>
                      {t('ops.ticket.pickFiles')}
                    </Button>
                  </Upload>
                </Form.Item>
                <Button type="primary" htmlType="submit" icon={<SendOutlined />} loading={submitting}>
                  {t('ops.ticket.submit')}
                </Button>
              </Form>
            )}
          </Card>
        </Col>
        <Col xs={24} xl={14}>
          <Card
            title={t('ops.ticket.myTickets')}
            extra={
              <Button
                icon={<ReloadOutlined />}
                aria-label={t('common.refresh')}
                title={t('common.refresh')}
                loading={list.isFetching}
                onClick={() => void list.refetch()}
              />
            }
          >
            <Table<Ticket>
              rowKey="id"
              size="small"
              columns={columns}
              dataSource={list.data?.items}
              loading={list.isFetching}
              scroll={{ x: 'max-content' }}
              rowClassName={(r) => (r.isOverdue && r.status !== 'dong' && r.status !== 'da_xu_ly' ? 'bd-row-overdue' : '')}
              onRow={(r) => ({ onClick: () => openTicket(r.id), style: { cursor: 'pointer' } })}
              pagination={{
                current: page,
                pageSize: 10,
                total: list.data?.total ?? 0,
                onChange: setPage,
                showTotal: (count) => t('common.totalItems', { count }),
              }}
              locale={{ emptyText: <Empty description={t('ops.ticket.emptyMine')} /> }}
            />
          </Card>
        </Col>
      </Row>
      <TicketDrawer id={openId} onClose={() => openTicket(null)} />
    </>
  )
}
