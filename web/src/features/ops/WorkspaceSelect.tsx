import { useEffect, useState } from 'react'
import { Select, Spin } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { qk } from '@/api/queryKeys'
import { listAllWorkspaces, listMyWorkspaces } from '@/api/workspaces'
import { useCan } from '@/hooks/usePermissions'

interface WorkspaceSelectProps {
  value?: string
  onChange?: (value: string | undefined) => void
  placeholder?: string
}

/**
 * Workspace (nhóm tài nguyên) select: administrators search every workspace (GET /workspaces/all),
 * other users pick among their own workspaces.
 */
export default function WorkspaceSelect({ value, onChange, placeholder }: WorkspaceSelectProps) {
  const { t } = useTranslation()
  const can = useCan()
  const isAdmin = can('workspace.admin')
  const [search, setSearch] = useState('')
  const [term, setTerm] = useState('')

  useEffect(() => {
    const timer = window.setTimeout(() => setTerm(search.trim()), 300)
    return () => window.clearTimeout(timer)
  }, [search])

  const all = useQuery({
    queryKey: [...qk.allWorkspaces, 'select', term],
    queryFn: async () => (await listAllWorkspaces({ q: term || undefined, page: 1, pageSize: 50 })).items,
    enabled: isAdmin,
  })
  const mine = useQuery({ queryKey: qk.myWorkspaces, queryFn: () => listMyWorkspaces(), enabled: !isAdmin })
  const source = isAdmin ? all : mine
  const items = (source.data ?? []).filter(
    (w) => isAdmin || !term || w.name.toLowerCase().includes(term.toLowerCase()),
  )

  return (
    <Select
      allowClear
      showSearch
      value={value}
      filterOption={false}
      searchValue={search}
      onSearch={setSearch}
      onChange={(v?: string) => onChange?.(v)}
      placeholder={placeholder}
      options={items.map((w) => ({ value: w.id, label: w.name }))}
      notFoundContent={source.isFetching ? <Spin size="small" /> : t('common.noMatch')}
      style={{ width: '100%' }}
      popupMatchSelectWidth={false}
    />
  )
}
