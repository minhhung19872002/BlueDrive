import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { Select, Space, Spin } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { qk } from '@/api/queryKeys'
import { listRoles } from '@/api/roles'
import { listUserGroups, lookupUsers } from '@/api/users'
import { listMyWorkspaces } from '@/api/workspaces'
import { useOrgTree } from '@/hooks/useLookups'
import type { OrganizationTreeNode } from '@/types/admin'
import type { PrincipalType } from '@/types/sharing'
import { normalizeText } from '@/utils/tree'
import type { PrincipalValue } from './levels'
import PrincipalIcon from './PrincipalIcon'

interface Option {
  value: string
  label: string
  type: PrincipalType
  hint?: string | null
}

function flattenOrgs(nodes: OrganizationTreeNode[], out: OrganizationTreeNode[] = []): OrganizationTreeNode[] {
  nodes.forEach((n) => {
    out.push(n)
    flattenOrgs(n.children, out)
  })
  return out
}

interface PrincipalSelectBaseProps {
  /** Principal kinds offered (users are searched on the server, groups/units/roles filtered here). */
  types: PrincipalType[]
  placeholder?: string
}

interface MultiProps extends PrincipalSelectBaseProps {
  multiple: true
  value?: PrincipalValue[]
  onChange?: (value: PrincipalValue[]) => void
}

interface SingleProps extends PrincipalSelectBaseProps {
  multiple?: false
  value?: PrincipalValue | null
  onChange?: (value: PrincipalValue | null) => void
}

/** Search box for users, user groups, organization units and roles (sharing targets / ACL principals). */
export default function PrincipalSelect(props: MultiProps | SingleProps) {
  const { types, placeholder } = props
  const { t } = useTranslation()
  const [search, setSearch] = useState('')
  const [term, setTerm] = useState('')

  useEffect(() => {
    const timer = window.setTimeout(() => setTerm(search.trim()), 300)
    return () => window.clearTimeout(timer)
  }, [search])

  const users = useQuery({
    queryKey: [...qk.users, 'lookup', term],
    queryFn: () => lookupUsers(term, 20),
    enabled: types.includes('user'),
  })
  const groups = useQuery({
    queryKey: [...qk.userGroups, 'search', term],
    queryFn: () => listUserGroups({ q: term, page: 1, pageSize: 20 }),
    enabled: types.includes('group'),
  })
  const orgTree = useOrgTree(false)
  const spaces = useQuery({ queryKey: [...qk.workspaces, 'group'], queryFn: () => listMyWorkspaces('group'), enabled: types.includes('space') })
  const roles = useQuery({ queryKey: [...qk.roles, 'list', ''], queryFn: () => listRoles(), enabled: types.includes('role') })

  const groupedOptions = useMemo(() => {
    const needle = normalizeText(term)
    const match = (s: string) => !needle || normalizeText(s).includes(needle)
    const sections: { type: PrincipalType; options: Option[] }[] = []
    if (types.includes('user'))
      sections.push({
        type: 'user',
        options: (users.data ?? []).map((u) => ({
          value: `user:${u.id}`,
          label: `${u.fullName} (${u.username})`,
          type: 'user' as const,
          hint: u.organizationName,
        })),
      })
    if (types.includes('group'))
      sections.push({
        type: 'group',
        options: (groups.data?.items ?? []).map((g) => ({ value: `group:${g.id}`, label: g.name, type: 'group' as const })),
      })
    if (types.includes('space'))
      sections.push({
        type: 'space',
        options: (spaces.data ?? [])
          .filter((w) => match(w.name))
          .slice(0, 20)
          .map((w) => ({ value: `space:${w.id}`, label: w.name, type: 'space' as const })),
      })
    if (types.includes('organization'))
      sections.push({
        type: 'organization',
        options: flattenOrgs(orgTree.data ?? [])
          .filter((o) => match(`${o.name} ${o.code}`))
          .slice(0, 20)
          .map((o) => ({ value: `organization:${o.id}`, label: o.name, type: 'organization' as const, hint: o.code })),
      })
    if (types.includes('role'))
      sections.push({
        type: 'role',
        options: (roles.data ?? [])
          .filter((r) => match(r.name))
          .map((r) => ({ value: `role:${r.id}`, label: r.name, type: 'role' as const })),
      })
    return sections
      .filter((s) => s.options.length > 0)
      .map((s) => ({
        label: t(`sharing.principalGroup.${s.type}`),
        title: s.type,
        options: s.options.map((o) => ({
          value: o.value,
          label: o.label,
          searchLabel: o.label,
          display: (
            <Space size={6}>
              <PrincipalIcon type={o.type} />
              <span>{o.label}</span>
              {o.hint && <span style={{ color: 'rgba(0,0,0,0.45)', fontSize: 12 }}>{o.hint}</span>}
            </Space>
          ),
        })),
      }))
  }, [types, users.data, groups.data, spaces.data, orgTree.data, roles.data, term, t])

  const loading = users.isFetching || groups.isFetching

  const common = {
    showSearch: true,
    labelInValue: true,
    filterOption: false,
    searchValue: search,
    onSearch: setSearch,
    options: groupedOptions,
    optionRender: (option: { data: { display?: ReactNode; label?: ReactNode } }) =>
      option.data.display ?? option.data.label,
    placeholder: placeholder ?? t('sharing.principalPlaceholder'),
    notFoundContent: loading ? <Spin size="small" /> : t('common.noMatch'),
    style: { width: '100%' },
    allowClear: true,
  }

  if (props.multiple) {
    return (
      <Select<PrincipalValue[]>
        {...common}
        mode="multiple"
        maxTagCount="responsive"
        value={props.value}
        onChange={(v) => {
          setSearch('')
          props.onChange?.((v ?? []).map((x) => ({ value: x.value, label: String(x.label) })))
        }}
      />
    )
  }
  const single = props as SingleProps
  return (
    <Select<PrincipalValue | null>
      {...common}
      value={single.value ?? null}
      onChange={(v) => single.onChange?.(v ? { value: v.value, label: String(v.label) } : null)}
    />
  )
}
