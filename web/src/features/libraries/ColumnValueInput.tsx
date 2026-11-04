import { DatePicker, Input, InputNumber, Select, Switch } from 'antd'
import dayjs from 'dayjs'
import { useTranslation } from 'react-i18next'
import type { ColumnValue, LibraryColumn } from '@/api/libraries'
import PrincipalSelect from '@/features/sharing/PrincipalSelect'
import { useCatalogItems } from '@/hooks/useLookups'

/** An editor for one value of a library column (details panel, quick-edit grid). */
export function ColumnValueInput({
  column,
  value,
  onChange,
  size,
}: {
  column: LibraryColumn
  value: ColumnValue
  onChange: (v: ColumnValue) => void
  size?: 'small'
}) {
  const { t } = useTranslation()
  const catalog = useCatalogItems(column.type === 'catalog' ? column.catalogId : null)
  const s = size
  switch (column.type) {
    case 'multiline_text':
      return <Input.TextArea size={s} autoSize={{ minRows: 1, maxRows: 4 }} value={(value as string) ?? ''} onChange={(e) => onChange(e.target.value || null)} aria-label={column.name} />
    case 'number':
      return <InputNumber size={s} style={{ width: '100%' }} value={value as number | null} onChange={(v) => onChange(v ?? null)} aria-label={column.name} />
    case 'date_time':
      return (
        <DatePicker
          size={s}
          showTime
          format="DD/MM/YYYY HH:mm"
          style={{ width: '100%' }}
          value={value ? dayjs(value as string) : null}
          onChange={(d) => onChange(d ? d.toISOString() : null)}
          aria-label={column.name}
        />
      )
    case 'boolean':
      return <Switch size={s === 'small' ? 'small' : 'default'} checked={value === true} onChange={(v) => onChange(v)} aria-label={column.name} />
    case 'choice':
      return <Select size={s} allowClear style={{ width: '100%' }} value={(value as string) ?? undefined} onChange={(v) => onChange(v ?? null)} options={column.choices.map((c) => ({ value: c, label: c }))} aria-label={column.name} />
    case 'multi_choice':
      return <Select size={s} mode="multiple" style={{ width: '100%' }} value={(value as string[]) ?? []} onChange={(v) => onChange(v.length ? v : null)} options={column.choices.map((c) => ({ value: c, label: c }))} aria-label={column.name} />
    case 'tags':
      return <Select size={s} mode="tags" style={{ width: '100%' }} value={(value as string[]) ?? []} onChange={(v) => onChange(v.length ? v : null)} aria-label={column.name} />
    case 'catalog':
      return (
        <Select
          size={s}
          allowClear
          showSearch
          optionFilterProp="label"
          style={{ width: '100%' }}
          value={(value as string) ?? undefined}
          onChange={(v) => onChange(v ?? null)}
          options={(catalog.data ?? []).map((i) => ({ value: i.id, label: i.name }))}
          aria-label={column.name}
        />
      )
    case 'person':
      return (
        <PrincipalSelect
          multiple
          types={['user']}
          value={value ? [{ value: `user:${value as string}`, label: t('library.column.person') }] : []}
          onChange={(v) => onChange(v.length ? v[v.length - 1].value.slice(5) : null)}
        />
      )
    default:
      return <Input size={s} value={(value as string) ?? ''} onChange={(e) => onChange(e.target.value || null)} aria-label={column.name} />
  }
}
