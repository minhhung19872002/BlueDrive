import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { getErrorMessage } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import { listUsers } from '@/api/users'
import PrintLayout from '@/components/print/PrintLayout'
import PrintTable, { type PrintColumn } from '@/components/print/PrintTable'
import { useAllUserGroups, useOrgTree, useRoles } from '@/hooks/useLookups'
import type { UserDto, UserFilter, UserStatus } from '@/types/admin'
import { formatBytes } from '@/utils/format'
import { boolParam, findInTree, PRINT_MAX_ROWS, strParam } from '@/utils/print'

export default function PrintUsersPage() {
  const { t } = useTranslation()
  const [params] = useSearchParams()
  const filter = useMemo<UserFilter>(
    () => ({
      q: strParam(params, 'q'),
      organizationId: strParam(params, 'organizationId'),
      includeSubOrganizations: boolParam(params, 'includeSubOrganizations'),
      roleId: strParam(params, 'roleId'),
      groupId: strParam(params, 'groupId'),
      status: strParam<UserStatus>(params, 'status'),
      sort: strParam(params, 'sort'),
    }),
    [params],
  )
  const tree = useOrgTree()
  const roles = useRoles()
  const groups = useAllUserGroups()
  const query = useQuery({
    queryKey: [...qk.users, 'print', filter],
    queryFn: () => listUsers({ ...filter, page: 1, pageSize: PRINT_MAX_ROWS }),
  })

  const filterText = [
    filter.q && t('print.keyword', { q: filter.q }),
    filter.organizationId &&
      t(filter.includeSubOrganizations ? 'print.orgWithChildren' : 'print.org', {
        name: findInTree(tree.data, filter.organizationId)?.name ?? '',
      }),
    filter.roleId && t('print.role', { name: roles.data?.find((r) => r.id === filter.roleId)?.name ?? '' }),
    filter.groupId && t('print.group', { name: groups.data?.find((g) => g.id === filter.groupId)?.name ?? '' }),
    filter.status && t(`user.status.${filter.status}`),
  ]
    .filter(Boolean)
    .join('; ')

  const columns: PrintColumn<UserDto>[] = [
    { key: 'username', title: t('user.username'), render: (r) => r.username, width: '12%' },
    { key: 'fullName', title: t('user.fullName'), render: (r) => r.fullName, width: '17%' },
    { key: 'org', title: t('user.organization'), render: (r) => r.organizationName ?? '', width: '17%' },
    { key: 'position', title: t('user.position'), render: (r) => r.position ?? '', width: '11%' },
    { key: 'contact', title: t('org.contact'), render: (r) => [r.email, r.phone].filter(Boolean).join(' - '), width: '17%' },
    { key: 'roles', title: t('user.roles'), render: (r) => r.roles.map((x) => x.name).join(', '), width: '12%' },
    { key: 'quota', title: t('user.quota'), render: (r) => formatBytes(r.quotaBytes), align: 'right', width: '7%' },
    { key: 'status', title: t('common.status'), render: (r) => t(`user.status.${r.status}`), width: '7%' },
  ]

  return (
    <PrintLayout
      title={t('user.printTitle')}
      filterText={filterText || undefined}
      ready={query.isSuccess && !tree.isLoading && !roles.isLoading && !groups.isLoading}
      loading={query.isLoading}
      error={query.isError ? getErrorMessage(query.error) : null}
    >
      <PrintTable columns={columns} rows={query.data?.items ?? []} rowKey={(r) => r.id} total={query.data?.total} />
    </PrintLayout>
  )
}
