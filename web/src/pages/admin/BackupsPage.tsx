import { useEffect, useRef, useState } from 'react'
import {
  Alert,
  App,
  Button,
  Card,
  Checkbox,
  Col,
  Descriptions,
  Dropdown,
  Empty,
  Form,
  Input,
  InputNumber,
  Radio,
  Row,
  Select,
  Space,
  Switch,
  Table,
  Tag,
  TimePicker,
  Tooltip,
  Typography,
} from 'antd'
import {
  CloudDownloadOutlined,
  CloudUploadOutlined,
  ExportOutlined,
  ReloadOutlined,
  SaveOutlined,
} from '@ant-design/icons'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import dayjs, { type Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'
import { getErrorMessage, showApiError } from '@/api/errors'
import {
  getBackupDownloadUrl,
  getBackupSchedule,
  listBackups,
  openSignedUrl,
  startBackup,
  startDataExport,
  updateBackupSchedule,
} from '@/api/operations'
import { qko } from '@/api/queryKeys.ops'
import PageHeader from '@/components/PageHeader'
import JobStatusTag from '@/features/ops/JobStatusTag'
import { useReportQueuedNotice } from '@/features/ops/useReportExport'
import { isJobActive } from '@/features/ops/utils'
import { useCan } from '@/hooks/usePermissions'
import type { BackupJob, BackupPart, BackupSchedule } from '@/types/ops'
import { formatBytes, formatDateTime } from '@/utils/format'

type Preset = 'daily' | 'weekly' | 'custom'

interface ScheduleForm {
  preset: Preset
  time: Dayjs
  weekday: number
  cron: string
  retentionCount: number
  fullDayOfWeek: number
  toS3: boolean
}

const DAYS = [0, 1, 2, 3, 4, 5, 6]

/** Recognise "m h * * *" (daily) and "m h * * d" (weekly); anything else is a custom cron. */
function parseCron(cron: string): Pick<ScheduleForm, 'preset' | 'time' | 'weekday' | 'cron'> {
  const parts = cron.trim().split(/\s+/)
  const base = { cron, time: dayjs().hour(2).minute(0), weekday: 0 }
  if (parts.length === 5 && /^\d{1,2}$/.test(parts[0]) && /^\d{1,2}$/.test(parts[1]) && parts[2] === '*' && parts[3] === '*') {
    const time = dayjs().hour(Number(parts[1])).minute(Number(parts[0]))
    if (parts[4] === '*') return { ...base, preset: 'daily', time }
    if (/^[0-6]$/.test(parts[4])) return { ...base, preset: 'weekly', time, weekday: Number(parts[4]) }
  }
  return { ...base, preset: 'custom' }
}

function buildCron(v: ScheduleForm): string {
  if (v.preset === 'custom') return v.cron.trim()
  const m = v.time.minute()
  const h = v.time.hour()
  return v.preset === 'daily' ? `${m} ${h} * * *` : `${m} ${h} * * ${v.weekday}`
}

function ScheduleCard({ schedule, canAdmin }: { schedule: BackupSchedule; canAdmin: boolean }) {
  const { t } = useTranslation()
  const { message } = App.useApp()
  const queryClient = useQueryClient()
  const [form] = Form.useForm<ScheduleForm>()
  const [saving, setSaving] = useState(false)
  const preset = Form.useWatch('preset', form)

  useEffect(() => {
    form.setFieldsValue({
      ...parseCron(schedule.cron),
      retentionCount: schedule.retentionCount,
      fullDayOfWeek: schedule.fullDayOfWeek,
      toS3: schedule.toS3,
    })
  }, [form, schedule])

  const save = async (v: ScheduleForm) => {
    setSaving(true)
    try {
      await updateBackupSchedule({ cron: buildCron(v), retentionCount: v.retentionCount, fullDayOfWeek: v.fullDayOfWeek, toS3: v.toS3 })
      void message.success(t('ops.backup.scheduleSaved'))
      await queryClient.invalidateQueries({ queryKey: qko.backupSchedule })
    } catch (error) {
      showApiError(error)
    } finally {
      setSaving(false)
    }
  }

  const dayOptions = DAYS.map((d) => ({ value: d, label: t(`ops.backup.days.${d}`) }))

  return (
    <Card size="small" title={t('ops.backup.scheduleTitle')} style={{ height: '100%' }}>
      <Descriptions size="small" column={1} style={{ marginBottom: 12 }}>
        <Descriptions.Item label={t('ops.backup.currentCron')}>
          <Typography.Text code>{schedule.cron}</Typography.Text>
        </Descriptions.Item>
        <Descriptions.Item label={t('ops.backup.nextRun')}>
          {schedule.nextRunAt ? formatDateTime(schedule.nextRunAt) : t('common.none')}
        </Descriptions.Item>
        <Descriptions.Item label={t('ops.backup.freeDisk')}>{formatBytes(schedule.freeDiskBytes)}</Descriptions.Item>
        <Descriptions.Item label={t('ops.backup.lastBackup')}>
          {schedule.lastBackup ? (
            <Space size={4} wrap>
              {formatDateTime(schedule.lastBackup.finishedAt ?? schedule.lastBackup.createdAt)}
              <JobStatusTag status={schedule.lastBackup.status} />
            </Space>
          ) : (
            t('common.none')
          )}
        </Descriptions.Item>
      </Descriptions>
      <Form<ScheduleForm> form={form} layout="vertical" onFinish={save} disabled={!canAdmin}>
        <Form.Item name="preset" label={t('ops.backup.frequency')}>
          <Radio.Group
            optionType="button"
            options={[
              { value: 'daily', label: t('ops.backup.presetDaily') },
              { value: 'weekly', label: t('ops.backup.presetWeekly') },
              { value: 'custom', label: t('ops.backup.presetCustom') },
            ]}
          />
        </Form.Item>
        {preset !== 'custom' ? (
          <Row gutter={12}>
            {preset === 'weekly' && (
              <Col xs={12}>
                <Form.Item name="weekday" label={t('ops.backup.weekday')}>
                  <Select options={dayOptions} />
                </Form.Item>
              </Col>
            )}
            <Col xs={12}>
              <Form.Item name="time" label={t('ops.backup.time')} rules={[{ required: true, message: t('ops.backup.timeRequired') }]}>
                <TimePicker format="HH:mm" minuteStep={5} style={{ width: '100%' }} allowClear={false} />
              </Form.Item>
            </Col>
          </Row>
        ) : (
          <Form.Item
            name="cron"
            label={t('ops.backup.cron')}
            extra={t('ops.backup.cronHint')}
            rules={[
              { required: true, message: t('ops.backup.cronRequired') },
              { pattern: /^\s*(\S+\s+){4}\S+\s*$/, message: t('ops.backup.cronInvalid') },
            ]}
          >
            <Input placeholder="0 2 * * *" style={{ fontFamily: 'monospace' }} />
          </Form.Item>
        )}
        <Row gutter={12}>
          <Col xs={12}>
            <Form.Item
              name="retentionCount"
              label={t('ops.backup.retention')}
              rules={[{ required: true, message: t('ops.backup.retentionRequired') }]}
            >
              <InputNumber min={1} max={365} style={{ width: '100%' }} />
            </Form.Item>
          </Col>
          <Col xs={12}>
            <Form.Item name="fullDayOfWeek" label={t('ops.backup.fullDay')} tooltip={t('ops.backup.fullDayHint')}>
              <Select options={dayOptions} />
            </Form.Item>
          </Col>
        </Row>
        <Form.Item name="toS3" label={t('ops.backup.toS3')} valuePropName="checked">
          <Switch />
        </Form.Item>
        {canAdmin && (
          <Button type="primary" htmlType="submit" icon={<SaveOutlined />} loading={saving}>
            {t('common.save')}
          </Button>
        )}
      </Form>
    </Card>
  )
}

function DataExportCard() {
  const { t } = useTranslation()
  const { modal } = App.useApp()
  const notify = useReportQueuedNotice()
  const [includeVersions, setIncludeVersions] = useState(false)
  const [includeAuditLogs, setIncludeAuditLogs] = useState(true)

  const start = () =>
    modal.confirm({
      title: t('ops.dataExport.confirmTitle'),
      content: t('ops.dataExport.confirm'),
      okText: t('ops.dataExport.start'),
      cancelText: t('common.cancel'),
      onOk: async () => {
        try {
          notify(await startDataExport(includeVersions, includeAuditLogs))
        } catch (error) {
          showApiError(error)
        }
      },
    })

  return (
    <Card size="small" title={t('ops.dataExport.title')}>
      <Typography.Paragraph type="secondary">{t('ops.dataExport.hint')}</Typography.Paragraph>
      <Space direction="vertical">
        <Checkbox checked={includeVersions} onChange={(e) => setIncludeVersions(e.target.checked)}>
          {t('ops.dataExport.includeVersions')}
        </Checkbox>
        <Checkbox checked={includeAuditLogs} onChange={(e) => setIncludeAuditLogs(e.target.checked)}>
          {t('ops.dataExport.includeAuditLogs')}
        </Checkbox>
        <Button icon={<ExportOutlined />} onClick={start}>
          {t('ops.dataExport.start')}
        </Button>
      </Space>
    </Card>
  )
}

/** Backup & restore (quality 3.5): schedule, manual backup, history with downloads; data export (4.2). */
export default function BackupsPage() {
  const { t } = useTranslation()
  const can = useCan()
  const canAdmin = can('backup.admin')
  const { modal, message } = App.useApp()
  const queryClient = useQueryClient()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const fullRef = useRef(false)

  const schedule = useQuery({ queryKey: qko.backupSchedule, queryFn: getBackupSchedule })
  const list = useQuery({
    queryKey: qko.backupList({ page, pageSize }),
    queryFn: () => listBackups({ page, pageSize }),
    placeholderData: keepPreviousData,
    refetchInterval: (q) => (q.state.data?.items.some((b) => isJobActive(b.status)) ? 5_000 : false),
  })
  const running = list.data?.items.some((b) => isJobActive(b.status)) ?? false

  // Refresh the schedule card (last backup, free disk) when a running backup finishes.
  const wasRunning = useRef(false)
  useEffect(() => {
    if (wasRunning.current && !running) void queryClient.invalidateQueries({ queryKey: qko.backupSchedule })
    wasRunning.current = running
  }, [queryClient, running])

  const backupNow = () => {
    fullRef.current = false
    modal.confirm({
      title: t('ops.backup.nowTitle'),
      content: (
        <Space direction="vertical">
          <span>{t('ops.backup.nowConfirm')}</span>
          <Checkbox onChange={(e) => (fullRef.current = e.target.checked)}>
            {t('ops.backup.forceFull')}
          </Checkbox>
        </Space>
      ),
      okText: t('ops.backup.now'),
      cancelText: t('common.cancel'),
      onOk: async () => {
        try {
          await startBackup(fullRef.current)
          void message.success(t('ops.backup.started'))
          setPage(1)
          await queryClient.invalidateQueries({ queryKey: qko.backups })
        } catch (error) {
          showApiError(error)
        }
      },
    })
  }

  const download = async (b: BackupJob, part: BackupPart) => {
    try {
      const res = await getBackupDownloadUrl(b.id, part)
      openSignedUrl(res.url)
    } catch (error) {
      showApiError(error)
    }
  }

  const latestOk = list.data?.items.find((b) => b.status === 'succeeded')

  const columns: ColumnsType<BackupJob> = [
    { title: t('ops.backup.createdAt'), dataIndex: 'createdAt', render: formatDateTime },
    {
      title: t('ops.backup.type'),
      dataIndex: 'type',
      render: (v: BackupJob['type'], r) => (
        <Space direction="vertical" size={0}>
          <span>{t(`ops.backup.types.${v}`)}</span>
          {r.requestedByName && (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {r.requestedByName}
            </Typography.Text>
          )}
        </Space>
      ),
    },
    {
      title: t('ops.backup.mode'),
      dataIndex: 'mode',
      render: (v: string) => <Tag color={v === 'full' ? 'geekblue' : 'cyan'}>{t(`ops.backup.modes.${v}`, { defaultValue: v })}</Tag>,
    },
    {
      title: t('common.status'),
      dataIndex: 'status',
      render: (v: BackupJob['status'], r) => (
        <Space size={4}>
          <JobStatusTag status={v} />
          {r.error && (
            <Tooltip title={r.error}>
              <Typography.Text type="danger" style={{ fontSize: 12 }}>
                {t('ops.report.errorDetail')}
              </Typography.Text>
            </Tooltip>
          )}
        </Space>
      ),
    },
    { title: t('ops.backup.startedAt'), dataIndex: 'startedAt', render: formatDateTime },
    { title: t('ops.backup.finishedAt'), dataIndex: 'finishedAt', render: formatDateTime },
    { title: t('ops.backup.size'), dataIndex: 'sizeBytes', align: 'right', render: (v: number) => (v > 0 ? formatBytes(v) : '') },
    {
      title: t('ops.backup.checksum'),
      dataIndex: 'checksum',
      render: (v: string | null) =>
        v ? (
          <Typography.Text className="bd-code-inline" copyable={{ text: v }} title={v}>
            {v.length > 16 ? `${v.slice(0, 16)}…` : v}
          </Typography.Text>
        ) : (
          ''
        ),
    },
    {
      title: t('ops.backup.id'),
      dataIndex: 'id',
      render: (v: string) => (
        <Typography.Text className="bd-code-inline" copyable={{ text: v }}>
          {v.slice(0, 8)}…
        </Typography.Text>
      ),
    },
    ...(canAdmin
      ? [
          {
            title: t('common.actions'),
            key: 'actions',
            fixed: 'right' as const,
            width: 100,
            render: (_: unknown, b: BackupJob) => (
              <Dropdown
                disabled={b.status !== 'succeeded'}
                trigger={['click']}
                menu={{
                  items: [
                    { key: 'db', label: t('ops.backup.partDb'), disabled: !b.hasDbDump },
                    { key: 'files', label: t('ops.backup.partFiles'), disabled: !b.hasFiles },
                    { key: 'manifest', label: t('ops.backup.partManifest') },
                  ],
                  onClick: ({ key }) => void download(b, key as BackupPart),
                }}
              >
                <Button size="small" icon={<CloudDownloadOutlined />}>
                  {t('ops.backup.download')}
                </Button>
              </Dropdown>
            ),
          },
        ]
      : []),
  ]

  const restoreCmd = `.\\scripts\\restore.ps1 ${latestOk?.id ?? '<backup-id>'}`

  return (
    <>
      <PageHeader
        title={t('menu.backups')}
        description={t('ops.backup.hint')}
        extra={
          <Space wrap>
            <Button
              icon={<ReloadOutlined />}
              loading={list.isFetching || schedule.isFetching}
              onClick={() => void queryClient.invalidateQueries({ queryKey: qko.backups })}
            >
              {t('common.refresh')}
            </Button>
            {canAdmin && (
              <Button type="primary" icon={<CloudUploadOutlined />} disabled={running} onClick={backupNow}>
                {t('ops.backup.now')}
              </Button>
            )}
          </Space>
        }
      />
      {running && <Alert type="info" showIcon message={t('ops.backup.runningNotice')} style={{ marginBottom: 16 }} />}
      {schedule.isError && <Alert type="error" showIcon message={getErrorMessage(schedule.error)} style={{ marginBottom: 16 }} />}

      <Row gutter={[16, 16]} style={{ marginBottom: 16 }}>
        <Col xs={24} xl={12}>
          {schedule.data ? (
            <ScheduleCard schedule={schedule.data} canAdmin={canAdmin} />
          ) : (
            <Card size="small" loading={schedule.isLoading} title={t('ops.backup.scheduleTitle')} />
          )}
        </Col>
        <Col xs={24} xl={12}>
          <Space direction="vertical" size={16} style={{ width: '100%' }}>
            <Card size="small" title={t('ops.backup.restoreTitle')}>
              <Typography.Paragraph>{t('ops.backup.restoreHint')}</Typography.Paragraph>
              <Typography.Paragraph>
                <Typography.Text code copyable={{ text: restoreCmd }}>
                  {restoreCmd}
                </Typography.Text>
              </Typography.Paragraph>
              {latestOk && (
                <Typography.Paragraph type="secondary" style={{ fontSize: 12, marginBottom: 0 }}>
                  {t('ops.backup.restoreLatest', { time: formatDateTime(latestOk.finishedAt ?? latestOk.createdAt) })}{' '}
                  <Typography.Text className="bd-code-inline" copyable>
                    {latestOk.id}
                  </Typography.Text>
                </Typography.Paragraph>
              )}
              <Alert type="warning" showIcon style={{ marginTop: 12 }} message={t('ops.backup.restoreWarning')} />
            </Card>
            {can('dataexport.admin') && <DataExportCard />}
          </Space>
        </Col>
      </Row>

      <Card size="small" title={t('ops.backup.historyTitle')}>
        <Table<BackupJob>
          rowKey="id"
          size="middle"
          columns={columns}
          dataSource={list.data?.items}
          loading={list.isLoading}
          scroll={{ x: 'max-content' }}
          pagination={{
            current: page,
            pageSize,
            total: list.data?.total ?? 0,
            showSizeChanger: true,
            pageSizeOptions: [10, 20, 50],
            showTotal: (count) => t('common.totalItems', { count }),
            onChange: (p, s) => {
              setPage(s !== pageSize ? 1 : p)
              setPageSize(s)
            },
          }}
          locale={{ emptyText: <Empty description={t('empty.backups')} /> }}
        />
      </Card>
    </>
  )
}
