import { useCallback, useMemo, useState } from 'react'
import type { TablePaginationConfig } from 'antd'
import type { SorterResult } from 'antd/es/table/interface'
import i18n from '@/i18n'

export const DEFAULT_PAGE_SIZE = 20

/**
 * State for a server-paged antd Table: filter object + page/pageSize/sort.
 * Changing the filter resets to page 1. Sort uses the column key, prefixed with "-" when descending.
 */
export function useServerTable<F extends object>(initialFilter: F, initialPageSize = DEFAULT_PAGE_SIZE) {
  const [filter, setFilterState] = useState<F>(initialFilter)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(initialPageSize)
  const [sort, setSort] = useState<string | undefined>()

  const setFilter = useCallback((patch: Partial<F>) => {
    setFilterState((f) => ({ ...f, ...patch }))
    setPage(1)
  }, [])

  const resetFilter = useCallback(() => {
    setFilterState(initialFilter)
    setPage(1)
    setSort(undefined)
    // initialFilter is expected to be a stable literal for the page lifetime.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const params = useMemo(() => ({ ...filter, sort, page, pageSize }), [filter, sort, page, pageSize])

  const onTableChange = useCallback(
    <T,>(pagination: TablePaginationConfig, _filters: unknown, sorter: SorterResult<T> | SorterResult<T>[]) => {
      const s = Array.isArray(sorter) ? sorter[0] : sorter
      const key = s?.columnKey ?? s?.field
      const nextSort = s?.order && key ? `${s.order === 'descend' ? '-' : ''}${String(key)}` : undefined
      if (nextSort !== sort) {
        setSort(nextSort)
        setPage(1)
      } else {
        setPage(pagination.current ?? 1)
      }
      if (pagination.pageSize && pagination.pageSize !== pageSize) {
        setPageSize(pagination.pageSize)
        setPage(1)
      }
    },
    [sort, pageSize],
  )

  const pagination = useCallback(
    (total: number | undefined): TablePaginationConfig => ({
      current: page,
      pageSize,
      total: total ?? 0,
      showSizeChanger: true,
      pageSizeOptions: [10, 20, 50, 100],
      showTotal: (count) => i18n.t('common.totalItems', { count }),
    }),
    [page, pageSize],
  )

  /** antd sortOrder for a column key, to keep the header arrow in sync with state. */
  const sortOrderOf = useCallback(
    (key: string) => (sort === key ? 'ascend' : sort === `-${key}` ? 'descend' : null),
    [sort],
  )

  return { filter, setFilter, resetFilter, params, onTableChange, pagination, sortOrderOf }
}

export type ServerTable<F extends object> = ReturnType<typeof useServerTable<F>>
