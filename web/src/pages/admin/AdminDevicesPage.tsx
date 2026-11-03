import { useState } from 'react'
import { Card, Input, Select, Space } from 'antd'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { listDevices } from '@/api/devices'
import { qk } from '@/api/queryKeys'
import PageHeader from '@/components/PageHeader'
import DeviceTable from '@/features/devices/DeviceTable'
import type { DeviceStatus } from '@/types/devices'

const STATUSES: DeviceStatus[] = ['active', 'wipe_pending', 'revoked', 'wiped']

/** Administrators: every BlueDrive Desktop installation, revoke / remote wipe. */
export default function AdminDevicesPage() {
  const { t } = useTranslation()
  const [query, setQuery] = useState('')
  const [status, setStatus] = useState<DeviceStatus | undefined>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const params = { query: query || undefined, status, page, pageSize }
  const { data, isLoading } = useQuery({
    queryKey: qk.devices(params),
    queryFn: () => listDevices(params),
    placeholderData: keepPreviousData,
  })
  return (
    <>
      <PageHeader title={t('menu.adminDevices')} description={t('devices.adminHint')} />
      <Card>
        <Space wrap style={{ marginBottom: 16 }}>
          <Input.Search
            allowClear
            placeholder={t('devices.searchPlaceholder')}
            onSearch={(v) => {
              setQuery(v.trim())
              setPage(1)
            }}
            style={{ width: 300 }}
          />
          <Select<DeviceStatus>
            allowClear
            placeholder={t('devices.allStatuses')}
            value={status}
            onChange={(v) => {
              setStatus(v)
              setPage(1)
            }}
            options={STATUSES.map((s) => ({ value: s, label: t(`devices.statuses.${s}`) }))}
            style={{ width: 200 }}
          />
        </Space>
        <DeviceTable
          devices={data?.items ?? []}
          loading={isLoading}
          showOwner
          invalidate={[['devices']]}
          pagination={{
            current: page,
            pageSize,
            total: data?.total ?? 0,
            showSizeChanger: true,
            onChange: (p, s) => {
              setPage(p)
              setPageSize(s)
            },
          }}
        />
      </Card>
    </>
  )
}
