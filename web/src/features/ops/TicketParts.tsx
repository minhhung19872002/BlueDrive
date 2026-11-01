import { Descriptions, Empty, List, Space, Tag, Timeline, Typography } from 'antd'
import { ClockCircleOutlined, PaperClipOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import type { Ticket, TicketPriority, TicketStatus } from '@/types/ops'
import { formatDateTime } from '@/utils/format'

const STATUS_COLORS: Record<TicketStatus, string> = {
  moi: 'blue',
  dang_xu_ly: 'processing',
  cho_phan_hoi: 'gold',
  da_xu_ly: 'green',
  dong: 'default',
}

export function TicketStatusTag({ status }: { status: TicketStatus }) {
  const { t } = useTranslation()
  return <Tag color={STATUS_COLORS[status]}>{t(`ops.ticket.status.${status}`)}</Tag>
}

export function TicketPriorityTag({ priority }: { priority: TicketPriority }) {
  const { t } = useTranslation()
  return <Tag color={priority === 'khan_cap' ? 'red' : 'default'}>{t(`ops.ticket.priority.${priority}`)}</Tag>
}

/** SLA due time, red with a "Quá hạn" tag when the ticket is overdue. */
export function TicketDue({ ticket }: { ticket: Ticket }) {
  const { t } = useTranslation()
  return ticket.isOverdue ? (
    <Space size={4} wrap>
      <Typography.Text type="danger">{formatDateTime(ticket.dueAt)}</Typography.Text>
      <Tag color="red">{t('ops.ticket.overdue')}</Tag>
    </Space>
  ) : (
    <span>{formatDateTime(ticket.dueAt)}</span>
  )
}

/** Ticket information, attachments and processing timeline (drawer body). */
export function TicketDetailView({ ticket }: { ticket: Ticket }) {
  const { t } = useTranslation()
  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Descriptions column={1} size="small" bordered>
        <Descriptions.Item label={t('ops.ticket.code')}>
          <Typography.Text copyable>{ticket.code}</Typography.Text>
        </Descriptions.Item>
        <Descriptions.Item label={t('ops.ticket.titleField')}>{ticket.title}</Descriptions.Item>
        <Descriptions.Item label={t('ops.ticket.priorityField')}>
          <TicketPriorityTag priority={ticket.priority} />
        </Descriptions.Item>
        <Descriptions.Item label={t('common.status')}>
          <TicketStatusTag status={ticket.status} />
        </Descriptions.Item>
        <Descriptions.Item label={t('ops.ticket.reporter')}>{ticket.reporterName}</Descriptions.Item>
        <Descriptions.Item label={t('ops.ticket.assignee')}>{ticket.assigneeName || t('ops.ticket.unassigned')}</Descriptions.Item>
        <Descriptions.Item label={t('ops.ticket.receivedAt')}>{formatDateTime(ticket.receivedAt)}</Descriptions.Item>
        <Descriptions.Item label={t('ops.ticket.dueAt')}>
          <TicketDue ticket={ticket} />
        </Descriptions.Item>
        <Descriptions.Item label={t('ops.ticket.resolvedAt')}>
          {ticket.resolvedAt ? (
            <>
              {formatDateTime(ticket.resolvedAt)}
              {ticket.resolutionHours !== null && ` (${t('ops.ticket.hours', { count: ticket.resolutionHours })})`}
            </>
          ) : (
            ''
          )}
        </Descriptions.Item>
      </Descriptions>
      <div>
        <Typography.Text strong>{t('ops.ticket.content')}</Typography.Text>
        <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', marginTop: 4 }}>{ticket.content}</Typography.Paragraph>
      </div>
      {ticket.resolution && (
        <div>
          <Typography.Text strong>{t('ops.ticket.resolution')}</Typography.Text>
          <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', marginTop: 4 }}>{ticket.resolution}</Typography.Paragraph>
        </div>
      )}
      {ticket.attachmentFileIds.length > 0 && (
        <div>
          <Typography.Text strong>{t('ops.ticket.attachments')}</Typography.Text>
          <List
            size="small"
            dataSource={ticket.attachmentFileIds}
            renderItem={(id, i) => (
              <List.Item>
                <Link to={`/files/${id}`}>
                  <PaperClipOutlined /> {t('ops.ticket.attachmentN', { n: i + 1 })}
                </Link>
              </List.Item>
            )}
          />
        </div>
      )}
      <div>
        <Typography.Text strong>{t('ops.ticket.timeline')}</Typography.Text>
        {ticket.events?.length ? (
          <Timeline
            style={{ marginTop: 12 }}
            items={ticket.events.map((e, i) => ({
              key: i,
              dot: i === (ticket.events?.length ?? 0) - 1 ? <ClockCircleOutlined /> : undefined,
              color: e.eventType === 'created' ? 'blue' : e.eventType === 'daxuly' || e.eventType === 'dong' ? 'green' : 'gray',
              children: (
                <Space direction="vertical" size={0}>
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {formatDateTime(e.occurredAt)}
                    {e.userName ? ` · ${e.userName}` : ''}
                  </Typography.Text>
                  <span style={{ whiteSpace: 'pre-wrap' }}>{e.note}</span>
                </Space>
              ),
            }))}
          />
        ) : (
          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('ops.ticket.noEvents')} />
        )}
      </div>
    </Space>
  )
}
