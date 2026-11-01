import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { getErrorMessage } from '@/api/errors'
import { listOrganizations } from '@/api/organizations'
import { qk } from '@/api/queryKeys'
import PrintLayout from '@/components/print/PrintLayout'
import PrintTable, { type PrintColumn } from '@/components/print/PrintTable'
import { useOrgTree } from '@/hooks/useLookups'
import type { OrganizationDto, OrganizationFilter, OrganizationType } from '@/types/admin'
import { boolParam, findInTree, PRINT_MAX_ROWS, strParam } from '@/utils/print'

export default function PrintOrganizationsPage() {
  const { t } = useTranslation()
  const [params] = useSearchParams()
  const filter = useMemo<OrganizationFilter>(
    () => ({
      q: strParam(params, 'q'),
      isActive: boolParam(params, 'isActive'),
      type: strParam<OrganizationType>(params, 'type'),
      parentId: strParam(params, 'parentId'),
      sort: strParam(params, 'sort'),
    }),
    [params],
  )
  const tree = useOrgTree()
  const query = useQuery({
    queryKey: [...qk.orgList, 'print', filter],
    queryFn: () => listOrganizations({ ...filter, page: 1, pageSize: PRINT_MAX_ROWS }),
  })

  const filterText = [
    filter.q && t('print.keyword', { q: filter.q }),
    filter.isActive !== undefined && (filter.isActive ? t('status.active') : t('status.inactive')),
    filter.type && t(`org.type.${filter.type}`),
    filter.parentId && t('print.parent', { name: findInTree(tree.data, filter.parentId)?.name ?? '' }),
  ]
    .filter(Boolean)
    .join('; ')

  const columns: PrintColumn<OrganizationDto>[] = [
    { key: 'code', title: t('org.code'), render: (r) => r.code, width: '10%' },
    { key: 'name', title: t('org.name'), render: (r) => r.name, width: '22%' },
    { key: 'type', title: t('org.typeLabel'), render: (r) => t(`org.type.${r.type}`), width: '9%' },
    { key: 'parent', title: t('org.parent'), render: (r) => r.parentName ?? '', width: '16%' },
    { key: 'contact', title: t('org.contact'), render: (r) => [r.phone, r.email].filter(Boolean).join(' - '), width: '16%' },
    { key: 'users', title: t('org.userCount'), render: (r) => r.userCount, align: 'right', width: '8%' },
    {
      key: 'status',
      title: t('common.status'),
      render: (r) => (r.isActive ? t('status.active') : t('status.inactive')),
      width: '12%',
    },
  ]

  return (
    <PrintLayout
      title={t('org.printTitle')}
      filterText={filterText || undefined}
      ready={query.isSuccess && !tree.isLoading}
      loading={query.isLoading}
      error={query.isError ? getErrorMessage(query.error) : null}
    >
      <PrintTable columns={columns} rows={query.data?.items ?? []} rowKey={(r) => r.id} total={query.data?.total} />
    </PrintLayout>
  )
}
