import { useQuery } from '@tanstack/react-query'
import { getBranding, getPasswordPolicy } from '@/api/auth'
import { listCatalogItems } from '@/api/catalogs'
import { getOrganizationTree } from '@/api/organizations'
import { qk } from '@/api/queryKeys'
import { listRoles } from '@/api/roles'
import { listUserGroups } from '@/api/users'

export function useBranding() {
  return useQuery({ queryKey: qk.branding, queryFn: getBranding, staleTime: 10 * 60_000, retry: 0 })
}

export function usePasswordPolicy() {
  return useQuery({ queryKey: qk.passwordPolicy, queryFn: getPasswordPolicy, staleTime: 10 * 60_000 })
}

export function useOrgTree(includeInactive = true) {
  return useQuery({
    queryKey: [...qk.orgTree, includeInactive],
    queryFn: () => getOrganizationTree(includeInactive),
  })
}

/** Items of a shared catalog (library "Tra cứu danh mục" columns). */
export function useCatalogItems(catalogId: string | null | undefined) {
  return useQuery({ queryKey: ['catalog-items', catalogId], queryFn: () => listCatalogItems(catalogId!, { isActive: true }), enabled: !!catalogId, staleTime: 5 * 60_000 })
}

export function useRoles() {
  return useQuery({ queryKey: [...qk.roles, 'list', ''], queryFn: () => listRoles() })
}

/** All user groups (for select boxes; max page size accepted by the server is 500). */
export function useAllUserGroups() {
  return useQuery({
    queryKey: [...qk.userGroups, 'all'],
    queryFn: async () => (await listUserGroups({ page: 1, pageSize: 500 })).items,
  })
}
