import { useState } from 'react'
import { Button, Card, Empty, Form, Input, Modal, Popconfirm, Segmented, Space, Table, Tag, Typography } from 'antd'
import { CheckOutlined, CloseOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { qk } from '@/api/queryKeys'
import { approveFile, listApprovals, rejectFile } from '@/api/sharing'
import PageHeader from '@/components/PageHeader'
import FileTypeIcon from '@/features/drive/components/FileTypeIcon'
import { APPROVAL_COLORS } from '@/features/drive/utils'
import FileViewerModal from '@/features/drive/viewer/FileViewerModal'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useServerTable } from '@/hooks/useServerTable'
import type { ApprovalStatus } from '@/types/drive'
import type { PendingApproval } from '@/types/sharing'
import { formatBytes, formatDateTime } from '@/utils/format'

/** Phê duyệt tư liệu: pending files of folders that require approval; approve or reject with a reason. */
export default function ApprovalsPage() {
  const { t } = useTranslation()
  const [status, setStatus] = useState<ApprovalStatus>('pending')
  const [viewing, setViewing] = useState<string | null>(null)
  const [rejecting, setRejecting] = useState<PendingApproval | null>(null)
  const [form] = Form.useForm<{ reason: string }>()
  const table = useServerTable<object>({})
  const params = { ...table.params, status }

  const list = useQuery({
    queryKey: [...qk.approvals, params],
    queryFn: () => listApprovals(params),
    placeholderData: keepPreviousData,
  })
  const items = list.data?.items ?? []

  const approve = useApiMutation(approveFile, { successMessage: t('approval.approved'), invalidate: [qk.drive] })
  const reject = useApiMutation(({ id, reason }: { id: string; reason: string }) => rejectFile(id, reason), {
    successMessage: t('approval.rejected'),
    invalidate: [qk.drive],
    onSuccess: () => {
      setRejecting(null)
      form.resetFields()
    },
  })

  return (
    <>
      <PageHeader
        title={t('menu.approvals')}
        description={t('approval.hint')}
        extra={
          <Segmented<ApprovalStatus>
            value={status}
            onChange={(v) => {
              setStatus(v)
              table.resetFilter()
            }}
            options={(['pending', 'rejected', 'approved'] as ApprovalStatus[]).map((s) => ({ value: s, label: t(`approval.status.${s}`) }))}
          />
        }
      />
      <Card styles={{ body: { padding: 0 } }}>
        <Table<PendingApproval>
          rowKey={(r) => r.item.id}
          dataSource={items}
          loading={list.isFetching}
          pagination={table.pagination(list.data?.total)}
          onChange={table.onTableChange}
          scroll={{ x: 860 }}
          locale={{
            emptyText: (
              <Empty description={status === 'pending' ? t('approval.empty') : t('common.noMatch')} style={{ padding: '32px 0' }} />
            ),
          }}
          columns={[
            {
              title: t('drive.colName'),
              key: 'name',
              ellipsis: true,
              render: (_, r) => (
                <Space>
                  <FileTypeIcon type="file" mediaType={r.item.mediaType} extension={r.item.extension} size={22} />
                  <Typography.Link onClick={() => setViewing(r.item.id)} title={t('approval.preview')}>
                    {r.item.name}
                  </Typography.Link>
                  {r.item.approvalStatus && (
                    <Tag color={APPROVAL_COLORS[r.item.approvalStatus]}>{t(`approval.status.${r.item.approvalStatus}`)}</Tag>
                  )}
                </Space>
              ),
            },
            {
              title: t('info.location'),
              key: 'location',
              width: 240,
              ellipsis: true,
              render: (_, r) => <span title={r.location}>{r.location}</span>,
            },
            { title: t('approval.uploader'), key: 'uploader', width: 170, ellipsis: true, render: (_, r) => r.item.createdByName ?? '—' },
            { title: t('approval.submittedAt'), key: 'submittedAt', width: 150, render: (_, r) => formatDateTime(r.submittedAt) },
            { title: t('drive.colSize'), key: 'size', width: 100, align: 'right', render: (_, r) => formatBytes(r.item.sizeBytes) },
            {
              title: t('common.actions'),
              key: 'actions',
              width: 200,
              render: (_, r) =>
                r.item.approvalStatus === 'pending' ? (
                  <Space>
                    <Popconfirm
                      title={t('approval.approveConfirm', { name: r.item.name })}
                      okText={t('approval.approve')}
                      cancelText={t('common.cancel')}
                      onConfirm={() => approve.mutateAsync(r.item.id).catch(() => undefined)}
                    >
                      <Button type="primary" size="small" icon={<CheckOutlined />}>
                        {t('approval.approve')}
                      </Button>
                    </Popconfirm>
                    <Button danger size="small" icon={<CloseOutlined />} onClick={() => setRejecting(r)}>
                      {t('approval.reject')}
                    </Button>
                  </Space>
                ) : null,
            },
          ]}
        />
      </Card>
      {viewing && <FileViewerModal fileId={viewing} siblings={items.map((i) => i.item.id)} onClose={() => setViewing(null)} />}
      <Modal
        open={!!rejecting}
        title={t('approval.rejectTitle', { name: rejecting?.item.name ?? '' })}
        okText={t('approval.reject')}
        okButtonProps={{ danger: true }}
        cancelText={t('common.cancel')}
        confirmLoading={reject.isPending}
        onOk={() => form.submit()}
        onCancel={() => setRejecting(null)}
        destroyOnHidden
      >
        <Form form={form} layout="vertical" onFinish={({ reason }) => rejecting && reject.mutate({ id: rejecting.item.id, reason: reason.trim() })}>
          <Form.Item
            name="reason"
            label={t('approval.reason')}
            rules={[
              { required: true, whitespace: true, message: t('approval.reasonRequired') },
              { max: 2000, message: t('validation.maxLength', { max: 2000 }) },
            ]}
          >
            <Input.TextArea rows={4} maxLength={2000} showCount autoFocus />
          </Form.Item>
        </Form>
      </Modal>
    </>
  )
}
