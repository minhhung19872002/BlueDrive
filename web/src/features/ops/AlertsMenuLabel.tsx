import { Badge } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { getUnacknowledgedAlertCount } from '@/api/monitoring'
import { qko } from '@/api/queryKeys.ops'
import { useCan } from '@/hooks/usePermissions'

/** Sider label of "Cảnh báo" with the unacknowledged-alert count (polled every 60s, needs alert.view). */
export default function AlertsMenuLabel({ label }: { label: string }) {
  const can = useCan()
  const enabled = can('alert.view')
  const count = useQuery({
    queryKey: qko.alertCount,
    queryFn: getUnacknowledgedAlertCount,
    enabled,
    refetchInterval: 60_000,
    staleTime: 30_000,
  })
  const n = enabled ? (count.data ?? 0) : 0
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8, width: '100%', justifyContent: 'space-between' }}>
      <span>{label}</span>
      {n > 0 && <Badge count={n} overflowCount={99} size="small" />}
    </span>
  )
}
