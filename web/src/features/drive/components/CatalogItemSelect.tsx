import { useMemo } from 'react'
import { TreeSelect } from 'antd'
import { useTranslation } from 'react-i18next'
import type { CatalogItemDto } from '@/types/admin'
import { buildTree, filterTreeNodeUnaccent, type TreeOf } from '@/utils/tree'
import { useCatalogOptions } from '../hooks'

interface CatalogItemSelectProps {
  value?: string[]
  onChange?: (value: string[]) => void
  placeholder?: string
}

interface Node {
  value: string
  title: string
  selectable?: boolean
  checkable?: boolean
  disableCheckbox?: boolean
  children?: Node[]
}

const toNodes = (items: TreeOf<CatalogItemDto>[]): Node[] =>
  items.map((i) => ({ value: i.id, title: i.name, children: i.children?.length ? toNodes(i.children) : undefined }))

/** Multi-select of catalog items grouped by catalog (GET /catalogs/options), used as file metadata. */
export default function CatalogItemSelect({ value, onChange, placeholder }: CatalogItemSelectProps) {
  const { t } = useTranslation()
  const options = useCatalogOptions()
  const treeData = useMemo<Node[]>(
    () =>
      (options.data ?? [])
        .filter((c) => c.items.length > 0)
        .map((c) => ({
          value: `catalog:${c.id}`,
          title: c.name,
          selectable: false,
          disableCheckbox: true,
          children: toNodes(buildTree(c.items, (a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name, 'vi'))),
        })),
    [options.data],
  )
  return (
    <TreeSelect
      multiple
      allowClear
      showSearch
      treeCheckable={false}
      value={value}
      onChange={(v: string[]) => onChange?.((v ?? []).filter((x) => !x.startsWith('catalog:')))}
      treeData={treeData}
      loading={options.isLoading}
      filterTreeNode={filterTreeNodeUnaccent}
      placeholder={placeholder ?? t('drive.catalogsPlaceholder')}
      style={{ width: '100%' }}
      maxTagCount="responsive"
      listHeight={300}
      notFoundContent={t('drive.noCatalogs')}
    />
  )
}
