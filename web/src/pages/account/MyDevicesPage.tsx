import { Alert, Button, Card } from 'antd'
import { ReloadOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { listMyDevices } from '@/api/devices'
import { qk } from '@/api/queryKeys'
import PageHeader from '@/components/PageHeader'
import DeviceTable from '@/features/devices/DeviceTable'

/** "Thiết bị đã kết nối": computers syncing the user's documents with BlueDrive Desktop. */
export default function MyDevicesPage() {
  const { t } = useTranslation()
  const { data, isLoading, isFetching, refetch } = useQuery({ queryKey: qk.myDevices, queryFn: listMyDevices })
  return (
    <>
      <PageHeader
        title={t('devices.myTitle')}
        description={t('devices.myHint')}
        extra={
          <Button icon={<ReloadOutlined />} onClick={() => void refetch()} loading={isFetching}>
            {t('common.refresh')}
          </Button>
        }
      />
      <Card>
        {!isLoading && (data ?? []).length === 0 && <Alert type="info" showIcon message={t('devices.none')} style={{ marginBottom: 16 }} />}
        <DeviceTable devices={data ?? []} loading={isLoading} invalidate={[qk.myDevices]} pagination={false} />
      </Card>
    </>
  )
}
