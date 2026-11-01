import { useEffect, useMemo, useState, type CSSProperties } from 'react'
import { Select, Spin } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { qk } from '@/api/queryKeys'
import { lookupUsers } from '@/api/users'

interface UserLookupSelectProps {
  value?: string | null
  onChange?: (value: string | undefined) => void
  placeholder?: string
  /** Label of the current value before any search (e.g. assignee name). */
  initialLabel?: string | null
  style?: CSSProperties
}

/** Single user select backed by GET /users/lookup?q= (debounced server search). */
export default function UserLookupSelect({ value, onChange, placeholder, initialLabel, style }: UserLookupSelectProps) {
  const { t } = useTranslation()
  const [search, setSearch] = useState('')
  const [term, setTerm] = useState('')
  const [known, setKnown] = useState<Record<string, string>>({})

  useEffect(() => {
    const timer = window.setTimeout(() => setTerm(search.trim()), 300)
    return () => window.clearTimeout(timer)
  }, [search])

  const results = useQuery({ queryKey: [...qk.users, 'lookup', term], queryFn: () => lookupUsers(term, 20) })

  const options = useMemo(() => {
    const map = new Map<string, string>()
    if (value) map.set(value, known[value] ?? initialLabel ?? value)
    results.data?.forEach((u) => map.set(u.id, `${u.fullName} (${u.username})${u.organizationName ? ` - ${u.organizationName}` : ''}`))
    return [...map.entries()].map(([id, label]) => ({ value: id, label }))
  }, [initialLabel, known, results.data, value])

  return (
    <Select
      allowClear
      showSearch
      value={value ?? undefined}
      filterOption={false}
      searchValue={search}
      onSearch={setSearch}
      onChange={(id?: string) => {
        const label = options.find((o) => o.value === id)?.label
        if (id && label) setKnown((k) => ({ ...k, [id]: label }))
        onChange?.(id)
      }}
      options={options}
      placeholder={placeholder}
      notFoundContent={results.isFetching ? <Spin size="small" /> : t('common.noMatch')}
      style={{ width: '100%', ...style }}
      popupMatchSelectWidth={false}
    />
  )
}
