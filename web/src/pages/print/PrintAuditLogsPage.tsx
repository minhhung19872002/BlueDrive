import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import dayjs from 'dayjs'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { getErrorMessage } from '@/api/errors'
import { searchAuditLogs } from '@/api/monitoring'
import { qko } from '@/api/queryKeys.ops'
import PrintLayout from '@/components/print/PrintLayout'
import PrintTable, { type PrintColumn } from '@/components/print/PrintTable'
import { useOrgTree } from '@/hooks/useLookups'
import type { AuditAction, AuditLog, AuditLogFilter, ResourceType } from '@/types/ops'
import { formatDateTime } from '@/utils/format'
import { boolParam, findInTree, PRINT_MAX_ROWS, strParam } from '@/utils/print'

export default function PrintAuditLogsPage() {
  const { t } = useTranslation()
  const [params] = useSearchParams()
  const filter = useMemo<AuditLogFilter>(
    () => ({
      userId: strParam(params, 'userId'),
      organizationId: strParam(params, 'organizationId'),
      includeSubOrganizations: boolParam(params, 'includeSubOrganizations'),
      from: strParam(params, 'from'),
      to: strParam(params, 'to'),
      actions: strParam(params, 'actions')?.split(',').filter(Boolean) as AuditAction[] | undefined,
      resourceType: strParam<ResourceType>(params, 'resourceType'),
      workspaceId: strParam(params, 'workspaceId'),
      q: strParam(params, 'q'),
      ip: strParam(params, 'ip'),
    }),
    [params],
  )
  const tree = useOrgTree()
  const query = useQuery({
    queryKey: qko.auditSearch({ filter, print: true }),
    queryFn: () => searchAuditLogs(filter, { page: 1, pageSize: PRINT_MAX_ROWS }),
  })

  const d = (v?: string) => (v ? dayjs(v).format('DD/MM/YYYY') : '...')
  const filterText = [
    (filter.from || filter.to) && t('ops.print.period', { from: d(filter.from), to: d(filter.to) }),
    filter.organizationId &&
      t(filter.includeSubOrganizations ? 'print.orgWithChildren' : 'print.org', {
        name: findInTree(tree.data, filter.organizationId)?.name ?? '',
      }),
    filter.actions?.length && t('ops.print.actions', { list: filter.actions.map((a) => t(`ops.auditAction.${a}`)).join(', ') }),
    filter.resourceType && t('ops.print.resourceType', { name: t(`ops.resourceType.${filter.resourceType}`) }),
    filter.q && t('print.keyword', { q: filter.q }),
    filter.ip && t('ops.print.ip', { ip: filter.ip }),
  ]
    .filter(Boolean)
    .join('; ')

  const columns: PrintColumn<AuditLog>[] = [
    { key: 'time', title: t('ops.audit.time'), render: (r) => formatDateTime(r.occurredAt), width: '12%' },
    { key: 'user', title: t('ops.audit.user'), render: (r) => r.fullName || r.username || t('ops.audit.system'), width: '15%' },
    { key: 'org', title: t('ops.audit.organization'), render: (r) => r.organizationName ?? '', width: '15%' },
    { key: 'action', title: t('ops.audit.action'), render: (r) => t(`ops.auditAction.${r.action}`), width: '11%' },
    { key: 'resource', title: t('ops.audit.resource'), render: (r) => r.resourceName ?? '', width: '24%' },
    { key: 'ws', title: t('ops.audit.workspace'), render: (r) => r.workspaceName ?? '', width: '11%' },
    { key: 'ip', title: t('ops.audit.ip'), render: (r) => r.ip ?? '', width: '7%' },
  ]

  return (
    <PrintLayout
      title={t('ops.audit.printTitle')}
      filterText={filterText || undefined}
      ready={query.isSuccess && !tree.isLoading}
      loading={query.isLoading}
      error={query.isError ? getErrorMessage(query.error) : null}
    >
      <PrintTable columns={columns} rows={query.data?.items ?? []} rowKey={(r) => String(r.id)} total={query.data?.total} />
    </PrintLayout>
  )
}
