import { Button, Popconfirm, Space, Table, Tag, Typography } from 'antd'
import { ClearOutlined, StopOutlined, WindowsOutlined } from '@ant-design/icons'
import type { ColumnsType, TablePaginationConfig } from 'antd/es/table'
import { useTranslation } from 'react-i18next'
import { revokeDevice, wipeDevice } from '@/api/devices'
import { useApiMutation } from '@/hooks/useApiMutation'
import type { DeviceDto, DeviceStatus } from '@/types/devices'
import { formatDateTime } from '@/utils/format'

const STATUS_COLORS: Record<DeviceStatus, string> = {
  active: 'green',
  revoked: 'default',
  wipe_pending: 'orange',
  wiped: 'red',
}

/** Connected BlueDrive Desktop computers with revoke / remote wipe (own devices, or every device for administrators). */
export default function DeviceTable({
  devices,
  loading,
  showOwner,
  invalidate,
  pagination,
}: {
  devices: DeviceDto[]
  loading: boolean
  showOwner?: boolean
  invalidate: readonly (readonly unknown[])[]
  pagination?: false | TablePaginationConfig
}) {
  const { t } = useTranslation()
  const revoke = useApiMutation(revokeDevice, {
    successMessage: t('devices.revoked'),
    invalidate: invalidate.map((k) => [...k]),
  })
  const wipe = useApiMutation(wipeDevice, {
    successMessage: t('devices.wipeRequested'),
    invalidate: invalidate.map((k) => [...k]),
  })

  const columns: ColumnsType<DeviceDto> = [
    {
      title: t('devices.name'),
      key: 'name',
      render: (_, d) => (
        <Space direction="vertical" size={0}>
          <Space wrap>
            <WindowsOutlined />
            <Typography.Text strong>{d.name}</Typography.Text>
            {d.isCurrent && <Tag color="blue">{t('devices.current')}</Tag>}
          </Space>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {[d.os, d.appVersion && `BlueDrive Desktop ${d.appVersion}`].filter(Boolean).join(' · ')}
          </Typography.Text>
        </Space>
      ),
    },
    ...(showOwner
      ? [
          {
            title: t('devices.owner'),
            key: 'owner',
            render: (_: unknown, d: DeviceDto) => `${d.userFullName} (${d.userName})`,
          },
        ]
      : []),
    {
      title: t('devices.status'),
      key: 'status',
      render: (_, d) => <Tag color={STATUS_COLORS[d.status]}>{t(`devices.statuses.${d.status}`)}</Tag>,
    },
    {
      title: t('devices.lastSeen'),
      key: 'lastSeen',
      render: (_, d) => formatDateTime(d.lastSeenAt),
    },
    {
      title: t('devices.lastIp'),
      key: 'ip',
      render: (_, d) => d.lastIp ?? t('common.none'),
    },
    {
      title: t('devices.connectedAt'),
      key: 'created',
      render: (_, d) => formatDateTime(d.createdAt),
    },
    {
      title: t('common.actions'),
      key: 'actions',
      render: (_, d) =>
        d.status === 'active' || d.status === 'revoked' ? (
          <Space wrap>
            {d.status === 'active' && (
              <Popconfirm
                title={t('devices.revokeConfirm', { name: d.name })}
                okText={t('devices.revoke')}
                okButtonProps={{ danger: true }}
                cancelText={t('common.cancel')}
                onConfirm={() => revoke.mutateAsync(d.id).catch(() => undefined)}
              >
                <Button size="small" icon={<StopOutlined />}>
                  {t('devices.revoke')}
                </Button>
              </Popconfirm>
            )}
            <Popconfirm
              title={t('devices.wipeConfirm', { name: d.name })}
              description={t('devices.wipeHint')}
              okText={t('devices.wipe')}
              okButtonProps={{ danger: true }}
              cancelText={t('common.cancel')}
              onConfirm={() => wipe.mutateAsync(d.id).catch(() => undefined)}
            >
              <Button size="small" danger icon={<ClearOutlined />}>
                {t('devices.wipe')}
              </Button>
            </Popconfirm>
          </Space>
        ) : (
          <Typography.Text type="secondary">
            {d.status === 'wipe_pending' ? t('devices.waitingWipe') : formatDateTime(d.wipedAt ?? d.revokedAt ?? d.lastSeenAt)}
          </Typography.Text>
        ),
    },
  ]

  return (
    <Table<DeviceDto> rowKey="id" columns={columns} dataSource={devices} loading={loading} pagination={pagination} scroll={{ x: 900 }} />
  )
}
