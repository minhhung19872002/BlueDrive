import { useState } from 'react'
import { App, Button, Card, Checkbox, Col, Empty, Form, Input, InputNumber, Modal, Row, Select, Space, Table, Tag, Typography } from 'antd'
import { PlusOutlined, ReloadOutlined, TagOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import { useTranslation } from 'react-i18next'
import { showApiError } from '@/api/errors'
import { applyFieldErrors } from '@/api/formErrors'
import { addChangeLog, getSystemInfo, listChangeLogs } from '@/api/operations'
import { qko } from '@/api/queryKeys.ops'
import PageHeader from '@/components/PageHeader'
import { useCan } from '@/hooks/usePermissions'
import { CHANGE_TYPES, type ChangeLog, type ChangeType } from '@/types/ops'
import { formatDateTime } from '@/utils/format'

const TYPE_COLORS: Record<ChangeType, string> = {
  deploy: 'blue',
  config: 'purple',
  feature: 'green',
  fix: 'orange',
  maintenance: 'default',
  security: 'red',
}

interface AddForm {
  version: string
  changeType: ChangeType
  description: string
  announce: boolean
  announceDays: number
}

/** System change log (quality 5.2): versions, upgrades and configuration changes. */
export default function ChangeLogsPage() {
  const { t } = useTranslation()
  const can = useCan()
  const { message } = App.useApp()
  const queryClient = useQueryClient()
  const [changeType, setChangeType] = useState<ChangeType | undefined>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [open, setOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const [form] = Form.useForm<AddForm>()

  const info = useQuery({ queryKey: qko.systemInfo, queryFn: getSystemInfo, staleTime: 5 * 60_000 })
  const list = useQuery({
    queryKey: qko.changeLogList({ changeType, page, pageSize }),
    queryFn: () => listChangeLogs({ changeType, page, pageSize }),
    placeholderData: keepPreviousData,
  })

  const openAdd = () => {
    form.resetFields()
    form.setFieldsValue({ version: info.data?.version ?? '', changeType: 'deploy', announce: false, announceDays: 3 })
    setOpen(true)
  }

  const save = async () => {
    let values: AddForm
    try {
      values = await form.validateFields()
    } catch {
      return
    }
    setSaving(true)
    try {
      await addChangeLog({
        version: values.version.trim(),
        changeType: values.changeType,
        description: values.description.trim(),
        announceDays: values.announce ? values.announceDays : null,
      })
      void message.success(t(values.announce ? 'ops.changeLog.addedAnnounced' : 'ops.changeLog.added'))
      setOpen(false)
      setPage(1)
      await queryClient.invalidateQueries({ queryKey: qko.changeLogs })
      if (values.announce) await queryClient.invalidateQueries({ queryKey: qko.announcements })
    } catch (error) {
      if (!applyFieldErrors(form, error, ['version', 'changeType', 'description', 'announceDays'])) showApiError(error)
    } finally {
      setSaving(false)
    }
  }

  const columns: ColumnsType<ChangeLog> = [
    { title: t('ops.changeLog.version'), dataIndex: 'version', render: (v: string) => <Typography.Text strong>{v}</Typography.Text> },
    {
      title: t('ops.changeLog.type'),
      dataIndex: 'changeType',
      render: (v: ChangeType) => <Tag color={TYPE_COLORS[v]}>{t(`ops.changeLog.types.${v}`, { defaultValue: v })}</Tag>,
    },
    {
      title: t('ops.changeLog.description'),
      dataIndex: 'description',
      render: (v: string) => <span style={{ whiteSpace: 'pre-wrap' }}>{v}</span>,
    },
    { title: t('ops.changeLog.appliedBy'), dataIndex: 'appliedByName', render: (v: string | null) => v || t('ops.changeLog.system') },
    { title: t('ops.changeLog.appliedAt'), dataIndex: 'appliedAt', width: 140, render: formatDateTime },
  ]

  return (
    <>
      <PageHeader
        title={t('menu.changeLogs')}
        description={t('ops.changeLog.hint')}
        extra={
          <Space wrap>
            <Button icon={<ReloadOutlined />} loading={list.isFetching} onClick={() => void list.refetch()}>
              {t('common.refresh')}
            </Button>
            {can('changelog.edit') && (
              <Button type="primary" icon={<PlusOutlined />} onClick={openAdd}>
                {t('ops.changeLog.add')}
              </Button>
            )}
          </Space>
        }
      />
      <Card size="small" style={{ marginBottom: 16 }} loading={info.isLoading}>
        {info.data ? (
          <Space wrap size="large">
            <span>
              <TagOutlined /> {t('ops.changeLog.running')}: <Typography.Text strong>{info.data.version}</Typography.Text>
            </span>
            <span>
              {t('ops.changeLog.gitSha')}: <Typography.Text code>{info.data.gitSha}</Typography.Text>
            </span>
            <span>
              {t('ops.changeLog.environment')}: <Tag>{info.data.environment}</Tag>
            </span>
            <span>
              {t('ops.changeLog.serverTime')}: {formatDateTime(info.data.serverTimeUtc)}
            </span>
          </Space>
        ) : (
          <Typography.Text type="secondary">{t('ops.changeLog.noInfo')}</Typography.Text>
        )}
      </Card>
      <Card size="small">
        <Row gutter={[12, 12]} style={{ marginBottom: 12 }}>
          <Col xs={24} sm={12} md={8} xl={6}>
            <Select
              allowClear
              style={{ width: '100%' }}
              placeholder={t('ops.changeLog.allTypes')}
              value={changeType}
              onChange={(v) => {
                setChangeType(v)
                setPage(1)
              }}
              options={CHANGE_TYPES.map((c) => ({ value: c, label: t(`ops.changeLog.types.${c}`) }))}
            />
          </Col>
        </Row>
        <Table<ChangeLog>
          rowKey="id"
          size="middle"
          columns={columns}
          dataSource={list.data?.items}
          loading={list.isFetching}
          scroll={{ x: 'max-content' }}
          pagination={{
            current: page,
            pageSize,
            total: list.data?.total ?? 0,
            showSizeChanger: true,
            pageSizeOptions: [10, 20, 50, 100],
            showTotal: (count) => t('common.totalItems', { count }),
            onChange: (p, s) => {
              setPage(s !== pageSize ? 1 : p)
              setPageSize(s)
            },
          }}
          locale={{ emptyText: <Empty description={t('empty.changeLogs')} /> }}
        />
      </Card>

      <Modal
        open={open}
        title={t('ops.changeLog.addTitle')}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        confirmLoading={saving}
        onOk={() => void save()}
        onCancel={() => setOpen(false)}
        destroyOnHidden
      >
        <Form<AddForm> form={form} layout="vertical" preserve={false}>
          <Row gutter={12}>
            <Col xs={12}>
              <Form.Item
                name="version"
                label={t('ops.changeLog.version')}
                rules={[{ required: true, whitespace: true, message: t('ops.changeLog.versionRequired') }]}
              >
                <Input maxLength={50} placeholder="1.2.0" />
              </Form.Item>
            </Col>
            <Col xs={12}>
              <Form.Item name="changeType" label={t('ops.changeLog.type')} rules={[{ required: true }]}>
                <Select options={CHANGE_TYPES.map((c) => ({ value: c, label: t(`ops.changeLog.types.${c}`) }))} />
              </Form.Item>
            </Col>
          </Row>
          <Form.Item
            name="description"
            label={t('ops.changeLog.description')}
            rules={[{ required: true, whitespace: true, message: t('ops.changeLog.descriptionRequired') }]}
          >
            <Input.TextArea rows={5} maxLength={4000} showCount />
          </Form.Item>
          <Space align="baseline" wrap>
            <Form.Item name="announce" valuePropName="checked" style={{ marginBottom: 0 }}>
              <Checkbox>{t('ops.changeLog.announce')}</Checkbox>
            </Form.Item>
            <Form.Item noStyle shouldUpdate={(a, b) => a.announce !== b.announce}>
              {({ getFieldValue }) =>
                getFieldValue('announce') ? (
                  <Form.Item name="announceDays" rules={[{ required: true, message: t('ops.changeLog.announceDaysRequired') }]} style={{ marginBottom: 0 }}>
                    <InputNumber min={1} max={90} precision={0} addonAfter={t('ops.changeLog.days')} style={{ width: 160 }} />
                  </Form.Item>
                ) : null
              }
            </Form.Item>
          </Space>
          <Typography.Paragraph type="secondary" style={{ fontSize: 12, marginTop: 8, marginBottom: 0 }}>
            {t('ops.changeLog.announceHint')}
          </Typography.Paragraph>
        </Form>
      </Modal>
    </>
  )
}
