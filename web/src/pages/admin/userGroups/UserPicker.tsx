import { useEffect, useMemo, useState } from 'react'
import { Select, Spin } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { qk } from '@/api/queryKeys'
import { lookupUsers } from '@/api/users'
import type { UserLookupDto } from '@/types/admin'

interface UserPickerProps {
  value?: string[]
  onChange?: (value: string[]) => void
  /** Users already selected (to render their labels before any search). */
  initialUsers?: UserLookupDto[]
}

const label = (u: UserLookupDto) => `${u.fullName} (${u.username})${u.organizationName ? ` - ${u.organizationName}` : ''}`

/** Multi-select user picker backed by GET /users/lookup?q= (debounced server search). */
export default function UserPicker({ value, onChange, initialUsers = [] }: UserPickerProps) {
  const { t } = useTranslation()
  const [search, setSearch] = useState('')
  const [term, setTerm] = useState('')
  const [known, setKnown] = useState<Record<string, UserLookupDto>>(() =>
    Object.fromEntries(initialUsers.map((u) => [u.id, u])),
  )

  useEffect(() => {
    const timer = window.setTimeout(() => setTerm(search.trim()), 300)
    return () => window.clearTimeout(timer)
  }, [search])

  const results = useQuery({
    queryKey: [...qk.users, 'lookup', term],
    queryFn: () => lookupUsers(term, 20),
  })

  const options = useMemo(() => {
    const map = new Map<string, UserLookupDto>()
    ;(value ?? []).forEach((id) => known[id] && map.set(id, known[id]))
    results.data?.forEach((u) => map.set(u.id, u))
    return [...map.values()].map((u) => ({ value: u.id, label: label(u) }))
  }, [known, results.data, value])

  return (
    <Select
      mode="multiple"
      allowClear
      showSearch
      value={value}
      filterOption={false}
      searchValue={search}
      onSearch={setSearch}
      onChange={(ids: string[]) => {
        const found = results.data?.filter((u) => ids.includes(u.id)) ?? []
        if (found.length) setKnown((k) => ({ ...k, ...Object.fromEntries(found.map((u) => [u.id, u])) }))
        onChange?.(ids)
      }}
      options={options}
      placeholder={t('group.membersPlaceholder')}
      notFoundContent={results.isFetching ? <Spin size="small" /> : t('common.noMatch')}
      style={{ width: '100%' }}
      maxTagCount="responsive"
    />
  )
}
