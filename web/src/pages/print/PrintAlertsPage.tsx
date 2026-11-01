import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import dayjs from 'dayjs'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { getErrorMessage } from '@/api/errors'
import { searchAlerts } from '@/api/monitoring'
import { qko } from '@/api/queryKeys.ops'
import PrintLayout from '@/components/print/PrintLayout'
import PrintTable, { type PrintColumn } from '@/components/print/PrintTable'
import type { AlertDto, AlertFilter, AlertSeverity, AlertType } from '@/types/ops'
import { formatDateTime } from '@/utils/format'
import { boolParam, PRINT_MAX_ROWS, strParam } from '@/utils/print'

export default function PrintAlertsPage() {
  const { t } = useTranslation()
  const [params] = useSearchParams()
  const filter = useMemo<AlertFilter>(
    () => ({
      type: strParam<AlertType>(params, 'type'),
      severity: strParam<AlertSeverity>(params, 'severity'),
      acknowledged: boolParam(params, 'acknowledged'),
      from: strParam(params, 'from'),
      to: strParam(params, 'to'),
    }),
    [params],
  )
  const query = useQuery({
    queryKey: qko.alertList({ filter, print: true }),
    queryFn: () => searchAlerts(filter, { page: 1, pageSize: PRINT_MAX_ROWS }),
  })

  const d = (v?: string) => (v ? dayjs(v).format('DD/MM/YYYY') : '...')
  const filterText = [
    (filter.from || filter.to) && t('ops.print.period', { from: d(filter.from), to: d(filter.to) }),
    filter.type && t(`ops.alertType.${filter.type}`),
    filter.severity && t(`ops.alertSeverity.${filter.severity}`),
    filter.acknowledged !== undefined && t(filter.acknowledged ? 'ops.alert.handled' : 'ops.alert.open'),
  ]
    .filter(Boolean)
    .join('; ')

  const columns: PrintColumn<AlertDto>[] = [
    { key: 'time', title: t('ops.alert.time'), render: (r) => formatDateTime(r.createdAt), width: '13%' },
    { key: 'type', title: t('ops.alert.type'), render: (r) => r.typeName || t(`ops.alertType.${r.type}`), width: '16%' },
    { key: 'severity', title: t('ops.alert.severity'), render: (r) => t(`ops.alertSeverity.${r.severity}`), width: '10%' },
    { key: 'message', title: t('ops.alert.message'), render: (r) => r.message, width: '41%' },
    {
      key: 'status',
      title: t('common.status'),
      render: (r) => (r.isAcknowledged ? `${t('ops.alert.handled')} (${r.acknowledgedByName ?? ''})` : t('ops.alert.open')),
      width: '15%',
    },
  ]

  return (
    <PrintLayout
      title={t('ops.alert.printTitle')}
      filterText={filterText || undefined}
      ready={query.isSuccess}
      loading={query.isLoading}
      error={query.isError ? getErrorMessage(query.error) : null}
    >
      <PrintTable columns={columns} rows={query.data?.items ?? []} rowKey={(r) => r.id} total={query.data?.total} />
    </PrintLayout>
  )
}
