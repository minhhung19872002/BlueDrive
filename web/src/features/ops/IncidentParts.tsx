import { useEffect, useState } from 'react'
import { App, Col, DatePicker, Descriptions, Empty, Form, Input, Modal, Row, Select, Space, Tag, Timeline, Typography } from 'antd'
import { ClockCircleOutlined } from '@ant-design/icons'
import dayjs, { type Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'
import { showApiError } from '@/api/errors'
import { applyFieldErrors } from '@/api/formErrors'
import { createIncident } from '@/api/operations'
import {
  INCIDENT_CATEGORIES,
  INCIDENT_SEVERITIES,
  type CreateIncident,
  type Incident,
  type IncidentCategory,
  type IncidentSeverity,
  type IncidentStatus,
} from '@/types/ops'
import { formatDateTime } from '@/utils/format'

const STATUS_COLORS: Record<IncidentStatus, string> = {
  reported: 'blue',
  investigating: 'processing',
  containing: 'orange',
  recovering: 'gold',
  resolved: 'green',
  closed: 'default',
}

const SEVERITY_COLORS: Record<IncidentSeverity, string> = { low: 'default', medium: 'gold', high: 'orange', critical: 'red' }

export function IncidentStatusTag({ status }: { status: IncidentStatus }) {
  const { t } = useTranslation()
  return <Tag color={STATUS_COLORS[status]}>{t(`ops.incident.status.${status}`)}</Tag>
}

export function IncidentSeverityTag({ severity }: { severity: IncidentSeverity }) {
  const { t } = useTranslation()
  return <Tag color={SEVERITY_COLORS[severity]}>{t(`ops.incident.severity.${severity}`)}</Tag>
}

function TextBlock({ label, value }: { label: string; value: string | null }) {
  if (!value) return null
  return (
    <div>
      <Typography.Text strong>{label}</Typography.Text>
      <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', marginTop: 4 }}>{value}</Typography.Paragraph>
    </div>
  )
}

/** Incident record (drawer body): fields, analysis/remediation and the handling timeline. */
export function IncidentDetailView({ incident }: { incident: Incident }) {
  const { t } = useTranslation()
  const events = incident.events ?? []
  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Descriptions column={1} size="small" bordered>
        <Descriptions.Item label={t('ops.incident.code')}>
          <Typography.Text copyable>{incident.code}</Typography.Text>
        </Descriptions.Item>
        <Descriptions.Item label={t('ops.incident.titleField')}>{incident.title}</Descriptions.Item>
        <Descriptions.Item label={t('ops.incident.categoryField')}>{t(`ops.incident.category.${incident.category}`)}</Descriptions.Item>
        <Descriptions.Item label={t('ops.incident.severityField')}>
          <IncidentSeverityTag severity={incident.severity} />
        </Descriptions.Item>
        <Descriptions.Item label={t('common.status')}>
          <IncidentStatusTag status={incident.status} />
        </Descriptions.Item>
        <Descriptions.Item label={t('ops.incident.detectedAt')}>{formatDateTime(incident.detectedAt)}</Descriptions.Item>
        <Descriptions.Item label={t('ops.incident.reportedAt')}>{formatDateTime(incident.reportedAt)}</Descriptions.Item>
        <Descriptions.Item label={t('ops.incident.reporter')}>{incident.reporterName}</Descriptions.Item>
        <Descriptions.Item label={t('ops.incident.handler')}>{incident.handlerName || t('ops.incident.unassigned')}</Descriptions.Item>
        {incident.alertMessage && <Descriptions.Item label={t('ops.incident.alert')}>{incident.alertMessage}</Descriptions.Item>}
        {incident.resolvedAt && <Descriptions.Item label={t('ops.incident.resolvedAt')}>{formatDateTime(incident.resolvedAt)}</Descriptions.Item>}
        {incident.closedAt && <Descriptions.Item label={t('ops.incident.closedAt')}>{formatDateTime(incident.closedAt)}</Descriptions.Item>}
      </Descriptions>
      <TextBlock label={t('ops.incident.description')} value={incident.description} />
      <TextBlock label={t('ops.incident.affectedSystems')} value={incident.affectedSystems} />
      <TextBlock label={t('ops.incident.cause')} value={incident.cause} />
      <TextBlock label={t('ops.incident.coordinatingParties')} value={incident.coordinatingParties} />
      <TextBlock label={t('ops.incident.remediation')} value={incident.remediation} />
      <TextBlock label={t('ops.incident.conclusion')} value={incident.conclusion} />
      <div>
        <Typography.Text strong>{t('ops.incident.timeline')}</Typography.Text>
        {events.length ? (
          <Timeline
            style={{ marginTop: 12 }}
            items={events.map((e, i) => ({
              key: i,
              dot: i === events.length - 1 ? <ClockCircleOutlined /> : undefined,
              color: e.eventType === 'created' ? 'blue' : e.eventType === 'resolved' || e.eventType === 'closed' ? 'green' : 'gray',
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
          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('ops.incident.noEvents')} />
        )}
      </div>
    </Space>
  )
}

interface CreateForm {
  title: string
  description: string
  category: IncidentCategory
  severity: IncidentSeverity
  detectedAt?: Dayjs
  affectedSystems?: string
}

/** Modal to open a new incident record (blank or pre-filled from an alert). */
export function CreateIncidentModal({
  open,
  draft,
  onClose,
  onCreated,
}: {
  open: boolean
  draft?: CreateIncident | null
  onClose: () => void
  onCreated: (incident: Incident) => void
}) {
  const { t } = useTranslation()
  const { message } = App.useApp()
  const [form] = Form.useForm<CreateForm>()
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    if (!open) return
    form.resetFields()
    form.setFieldsValue({
      title: draft?.title ?? '',
      description: draft?.description ?? '',
      category: draft?.category ?? 'other',
      severity: draft?.severity ?? 'medium',
      detectedAt: draft?.detectedAt ? dayjs(draft.detectedAt) : dayjs(),
      affectedSystems: draft?.affectedSystems ?? undefined,
    })
  }, [open, draft, form])

  const save = async () => {
    let v: CreateForm
    try {
      v = await form.validateFields()
    } catch {
      return
    }
    setSaving(true)
    try {
      const created = await createIncident({
        title: v.title.trim(),
        description: v.description.trim(),
        category: v.category,
        severity: v.severity,
        detectedAt: v.detectedAt?.toISOString() ?? null,
        affectedSystems: v.affectedSystems?.trim() || null,
        alertId: draft?.alertId ?? null,
      })
      void message.success(t('ops.incident.created', { code: created.code }))
      onCreated(created)
    } catch (error) {
      if (!applyFieldErrors(form, error, ['title', 'description', 'category', 'severity', 'detectedAt', 'affectedSystems'])) showApiError(error)
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal
      open={open}
      title={draft?.alertId ? t('ops.incident.createFromAlertTitle') : t('ops.incident.createTitle')}
      okText={t('ops.incident.create')}
      cancelText={t('common.cancel')}
      confirmLoading={saving}
      onOk={() => void save()}
      onCancel={onClose}
      width={640}
      destroyOnHidden
    >
      <Form<CreateForm> form={form} layout="vertical" preserve={false}>
        <Form.Item name="title" label={t('ops.incident.titleField')} rules={[{ required: true, whitespace: true, message: t('ops.incident.titleRequired') }]}>
          <Input maxLength={300} />
        </Form.Item>
        <Row gutter={12}>
          <Col xs={24} sm={12}>
            <Form.Item name="category" label={t('ops.incident.categoryField')} rules={[{ required: true }]}>
              <Select options={INCIDENT_CATEGORIES.map((c) => ({ value: c, label: t(`ops.incident.category.${c}`) }))} />
            </Form.Item>
          </Col>
          <Col xs={24} sm={12}>
            <Form.Item name="severity" label={t('ops.incident.severityField')} rules={[{ required: true }]} extra={t('ops.incident.severityHint')}>
              <Select options={INCIDENT_SEVERITIES.map((s) => ({ value: s, label: t(`ops.incident.severity.${s}`) }))} />
            </Form.Item>
          </Col>
        </Row>
        <Form.Item name="detectedAt" label={t('ops.incident.detectedAt')}>
          <DatePicker showTime format="DD/MM/YYYY HH:mm" style={{ width: '100%' }} disabledDate={(d) => d.isAfter(dayjs(), 'day')} />
        </Form.Item>
        <Form.Item
          name="description"
          label={t('ops.incident.description')}
          rules={[{ required: true, whitespace: true, message: t('ops.incident.descriptionRequired') }]}
        >
          <Input.TextArea rows={4} maxLength={10000} showCount placeholder={t('ops.incident.descriptionPlaceholder')} />
        </Form.Item>
        <Form.Item name="affectedSystems" label={t('ops.incident.affectedSystems')}>
          <Input.TextArea rows={2} maxLength={2000} placeholder={t('ops.incident.affectedPlaceholder')} />
        </Form.Item>
      </Form>
    </Modal>
  )
}
