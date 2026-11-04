import { useState } from 'react'
import { App, Button, Card, Col, DatePicker, Empty, Form, Input, Modal, Row, Select, Space, Switch, Table, Tag, Typography } from 'antd'
import { DeleteOutlined, EditOutlined, PlusOutlined, ReloadOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import dayjs, { type Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'
import { showApiError } from '@/api/errors'
import { applyFieldErrors } from '@/api/formErrors'
import { deleteAnnouncement, listAnnouncements, saveAnnouncement } from '@/api/operations'
import { qko } from '@/api/queryKeys.ops'
import PageHeader from '@/components/PageHeader'
import { ANNOUNCEMENT_LEVEL_COLORS } from '@/features/ops/utils'
import { useCan } from '@/hooks/usePermissions'
import { ANNOUNCEMENT_LEVELS, type Announcement, type AnnouncementLevel } from '@/types/ops'
import { formatDateTime } from '@/utils/format'

interface AnnouncementForm {
  title: string
  content: string
  level: AnnouncementLevel
  window: [Dayjs | null, Dayjs | null] | null
  isActive: boolean
}

type DisplayState = 'current' | 'scheduled' | 'expired' | 'off'

function displayState(a: Announcement): DisplayState {
  if (!a.isActive) return 'off'
  if (a.isCurrent) return 'current'
  return dayjs(a.startsAt).isAfter(dayjs()) ? 'scheduled' : 'expired'
}

const STATE_COLORS: Record<DisplayState, string> = { current: 'green', scheduled: 'blue', expired: 'default', off: 'default' }

/** Quality 5.2: system announcements shown as a banner to every signed-in user (maintenance windows, upgrades, notices). */
export default function AnnouncementsPage() {
  const { t } = useTranslation()
  const can = useCan()
  const { message, modal } = App.useApp()
  const queryClient = useQueryClient()
  const [q, setQ] = useState<string | undefined>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [editing, setEditing] = useState<Announcement | 'new' | null>(null)
  const [saving, setSaving] = useState(false)
  const [form] = Form.useForm<AnnouncementForm>()
  const canEdit = can('announcement.edit')

  const list = useQuery({
    queryKey: qko.announcementList({ q, page, pageSize }),
    queryFn: () => listAnnouncements({ q, page, pageSize }),
    placeholderData: keepPreviousData,
  })

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: qko.announcements })
    await queryClient.invalidateQueries({ queryKey: qko.activeAnnouncements })
  }

  const openEditor = (a: Announcement | 'new') => {
    form.resetFields()
    form.setFieldsValue(
      a === 'new'
        ? { title: '', content: '', level: 'info', window: [dayjs(), null], isActive: true }
        : {
            title: a.title,
            content: a.content,
            level: a.level,
            window: [dayjs(a.startsAt), a.endsAt ? dayjs(a.endsAt) : null],
            isActive: a.isActive,
          },
    )
    setEditing(a)
  }

  const save = async () => {
    let v: AnnouncementForm
    try {
      v = await form.validateFields()
    } catch {
      return
    }
    setSaving(true)
    try {
      await saveAnnouncement(editing === 'new' || !editing ? null : editing.id, {
        title: v.title.trim(),
        content: v.content.trim(),
        level: v.level,
        startsAt: v.window?.[0]?.toISOString() ?? null,
        endsAt: v.window?.[1]?.toISOString() ?? null,
        isActive: v.isActive,
      })
      void message.success(t('ops.announcement.saved'))
      setEditing(null)
      await refresh()
    } catch (error) {
      if (!applyFieldErrors(form, error, ['title', 'content', 'level'])) showApiError(error)
    } finally {
      setSaving(false)
    }
  }

  const remove = (a: Announcement) =>
    modal.confirm({
      title: t('ops.announcement.deleteTitle'),
      content: t('ops.announcement.deleteConfirm', { title: a.title }),
      okText: t('common.delete'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: async () => {
        try {
          await deleteAnnouncement(a.id)
          void message.success(t('ops.announcement.deleted'))
          await refresh()
        } catch (error) {
          showApiError(error)
        }
      },
    })

  const columns: ColumnsType<Announcement> = [
    {
      title: t('ops.announcement.titleField'),
      dataIndex: 'title',
      render: (v: string, r) => (
        <Space direction="vertical" size={0}>
          <Typography.Text strong>{v}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12, whiteSpace: 'pre-wrap' }} ellipsis={{ tooltip: r.content }}>
            {r.content}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: t('ops.announcement.level'),
      dataIndex: 'level',
      render: (v: AnnouncementLevel) => <Tag color={ANNOUNCEMENT_LEVEL_COLORS[v]}>{t(`ops.announcement.levels.${v}`)}</Tag>,
    },
    { title: t('ops.announcement.startsAt'), dataIndex: 'startsAt', render: formatDateTime },
    {
      title: t('ops.announcement.endsAt'),
      dataIndex: 'endsAt',
      render: (v: string | null) => (v ? formatDateTime(v) : <Typography.Text type="secondary">{t('ops.announcement.noEnd')}</Typography.Text>),
    },
    {
      title: t('common.status'),
      key: 'state',
      render: (_, r) => {
        const s = displayState(r)
        return <Tag color={STATE_COLORS[s]}>{t(`ops.announcement.state.${s}`)}</Tag>
      },
    },
    { title: t('ops.announcement.createdBy'), dataIndex: 'createdByName' },
    ...(canEdit
      ? [
          {
            title: t('common.actions'),
            key: 'actions',
            fixed: 'right' as const,
            render: (_: unknown, r: Announcement) => (
              <Space size={4}>
                <Button size="small" icon={<EditOutlined />} aria-label={t('common.edit')} title={t('common.edit')} onClick={() => openEditor(r)} />
                <Button
                  size="small"
                  danger
                  icon={<DeleteOutlined />}
                  aria-label={t('common.delete')}
                  title={t('common.delete')}
                  onClick={() => remove(r)}
                />
              </Space>
            ),
          },
        ]
      : []),
  ]

  return (
    <>
      <PageHeader
        title={t('menu.announcements')}
        description={t('ops.announcement.hint')}
        extra={
          <Space wrap>
            <Button icon={<ReloadOutlined />} loading={list.isFetching} onClick={() => void list.refetch()}>
              {t('common.refresh')}
            </Button>
            {canEdit && (
              <Button type="primary" icon={<PlusOutlined />} onClick={() => openEditor('new')}>
                {t('ops.announcement.add')}
              </Button>
            )}
          </Space>
        }
      />
      <Card size="small">
        <Row gutter={[12, 12]} style={{ marginBottom: 12 }}>
          <Col xs={24} md={12} xl={8}>
            <Input.Search
              allowClear
              placeholder={t('ops.announcement.searchPlaceholder')}
              onSearch={(v) => {
                setQ(v.trim() || undefined)
                setPage(1)
              }}
            />
          </Col>
        </Row>
        <Table<Announcement>
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
          locale={{ emptyText: <Empty description={t('ops.announcement.empty')} /> }}
        />
      </Card>

      <Modal
        open={!!editing}
        title={editing === 'new' ? t('ops.announcement.addTitle') : t('ops.announcement.editTitle')}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        confirmLoading={saving}
        onOk={() => void save()}
        onCancel={() => setEditing(null)}
        width={640}
        destroyOnHidden
      >
        <Form<AnnouncementForm> form={form} layout="vertical" preserve={false}>
          <Form.Item
            name="title"
            label={t('ops.announcement.titleField')}
            rules={[{ required: true, whitespace: true, message: t('ops.announcement.titleRequired') }]}
          >
            <Input maxLength={200} />
          </Form.Item>
          <Form.Item
            name="content"
            label={t('ops.announcement.content')}
            rules={[{ required: true, whitespace: true, message: t('ops.announcement.contentRequired') }]}
          >
            <Input.TextArea rows={4} maxLength={2000} showCount placeholder={t('ops.announcement.contentPlaceholder')} />
          </Form.Item>
          <Row gutter={12}>
            <Col xs={24} sm={10}>
              <Form.Item name="level" label={t('ops.announcement.level')} rules={[{ required: true }]}>
                <Select options={ANNOUNCEMENT_LEVELS.map((l) => ({ value: l, label: t(`ops.announcement.levels.${l}`) }))} />
              </Form.Item>
            </Col>
            <Col xs={24} sm={14}>
              <Form.Item name="isActive" label={t('ops.announcement.active')} valuePropName="checked">
                <Switch />
              </Form.Item>
            </Col>
          </Row>
          <Form.Item
            name="window"
            label={t('ops.announcement.window')}
            extra={t('ops.announcement.windowHint')}
            rules={[
              {
                validator: (_, v: AnnouncementForm['window']) =>
                  v?.[0] && v?.[1] && !v[1].isAfter(v[0]) ? Promise.reject(new Error(t('ops.announcement.windowInvalid'))) : Promise.resolve(),
              },
            ]}
          >
            <DatePicker.RangePicker
              showTime
              format="DD/MM/YYYY HH:mm"
              allowEmpty={[true, true]}
              placeholder={[t('ops.announcement.startsAt'), t('ops.announcement.endsAt')]}
              style={{ width: '100%' }}
            />
          </Form.Item>
        </Form>
      </Modal>
    </>
  )
}
