import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { getErrorMessage } from '@/api/errors'
import { getStorageStats } from '@/api/monitoring'
import { qko } from '@/api/queryKeys.ops'
import PrintLayout from '@/components/print/PrintLayout'
import PrintTable, { type PrintColumn } from '@/components/print/PrintTable'
import { useOrgTree } from '@/hooks/useLookups'
import type { StatsBy, UsageRow } from '@/types/ops'
import { formatBytes } from '@/utils/format'
import { findInTree, PRINT_MAX_ROWS, strParam } from '@/utils/print'

const BYS: StatsBy[] = ['user', 'organization', 'workspace']

export default function PrintStorageStatsPage() {
  const { t } = useTranslation()
  const [params] = useSearchParams()
  const rawBy = strParam<StatsBy>(params, 'by')
  const by: StatsBy = rawBy && BYS.includes(rawBy) ? rawBy : 'user'
  const organizationId = strParam(params, 'organizationId')
  const q = strParam(params, 'q')
  const tree = useOrgTree()
  const query = useQuery({
    queryKey: qko.storageStatsList({ by, organizationId, q, print: true }),
    queryFn: () => getStorageStats({ by, organizationId, q, page: 1, pageSize: PRINT_MAX_ROWS }),
  })

  const filterText = [
    organizationId && t('print.orgWithChildren', { name: findInTree(tree.data, organizationId)?.name ?? '' }),
    q && t('print.keyword', { q }),
  ]
    .filter(Boolean)
    .join('; ')

  const columns: PrintColumn<UsageRow>[] = [
    { key: 'name', title: t(`ops.stats.nameOf.${by}`), render: (r) => r.name, width: '22%' },
    { key: 'code', title: t('ops.stats.code'), render: (r) => r.code ?? '', width: '12%' },
    { key: 'extra', title: t(`ops.stats.extraOf.${by}`), render: (r) => r.extra ?? '', width: '18%' },
    { key: 'used', title: t('ops.stats.used'), render: (r) => formatBytes(r.usedBytes), align: 'right', width: '10%' },
    { key: 'quota', title: t('ops.stats.quota'), render: (r) => formatBytes(r.quotaBytes), align: 'right', width: '10%' },
    {
      key: 'remaining',
      title: t('ops.stats.remaining'),
      render: (r) => formatBytes(Math.max(0, r.quotaBytes - r.usedBytes)),
      align: 'right',
      width: '10%',
    },
    { key: 'percent', title: t('ops.stats.percent'), render: (r) => `${r.percent.toLocaleString('vi-VN')}%`, align: 'right', width: '7%' },
    { key: 'files', title: t('ops.stats.files'), render: (r) => r.files.toLocaleString('vi-VN'), align: 'right', width: '6%' },
  ]

  return (
    <PrintLayout
      title={t(`ops.stats.printTitle.${by}`)}
      filterText={filterText || undefined}
      ready={query.isSuccess && !tree.isLoading}
      loading={query.isLoading}
      error={query.isError ? getErrorMessage(query.error) : null}
    >
      <PrintTable columns={columns} rows={query.data?.items ?? []} rowKey={(r) => r.id} total={query.data?.total} />
    </PrintLayout>
  )
}
