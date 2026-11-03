import { useState, type ReactNode } from 'react'
import { Button, Grid, Space, Table, Typography, type TableProps } from 'antd'
import { MoreOutlined, StarFilled } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import type { DriveItem } from '@/types/drive'
import { formatBytes, formatDateTime } from '@/utils/format'
import type { DriveActions, ItemFlags } from '../actions/useDriveActions'
import FileTypeIcon from '../components/FileTypeIcon'
import PositionedMenu, { type MenuPosition } from '../explorer/PositionedMenu'
import FitDropdown from '../components/FitDropdown'

/** Row of a flat list: the item plus optional page-specific data. */
export interface DriveRow<T = unknown> {
  key: string
  item: DriveItem
  extra?: T
}

interface DriveItemTableProps<T> {
  rows: DriveRow<T>[]
  loading: boolean
  actions: DriveActions
  flagsFor?: (row: DriveRow<T>) => ItemFlags
  /** Columns inserted after the name column. */
  extraColumns?: TableProps<DriveRow<T>>['columns']
  /** Date column (default: last modified). */
  dateColumn?: { title: string; value: (row: DriveRow<T>) => string | null }
  pagination?: TableProps<DriveRow<T>>['pagination']
  onChange?: TableProps<DriveRow<T>>['onChange']
  empty: ReactNode
}

/** Flat item list (Gần đây, Quan trọng, Được chia sẻ) with the same actions/context menu as the explorer. */
export default function DriveItemTable<T>({
  rows,
  loading,
  actions,
  flagsFor,
  extraColumns = [],
  dateColumn,
  pagination = false,
  onChange,
  empty,
}: DriveItemTableProps<T>) {
  const { t } = useTranslation()
  const screens = Grid.useBreakpoint()
  const isMobile = screens.md === false
  const [menu, setMenu] = useState<{ row: DriveRow<T>; pos: MenuPosition } | null>(null)

  const menuFor = (row: DriveRow<T>) => ({
    items: actions.menuItems([row.item], flagsFor?.(row)),
    onClick: ({ key }: { key: string }) => actions.run(key, [row.item]),
  })

  const columns: TableProps<DriveRow<T>>['columns'] = [
    {
      title: t('drive.colName'),
      key: 'name',
      ellipsis: true,
      render: (_, { item }) => (
        <Space size={10} style={{ minWidth: 0 }}>
          <FileTypeIcon type={item.type} mediaType={item.mediaType} extension={item.extension} size={22} />
          <Typography.Link
            ellipsis
            style={{ maxWidth: isMobile ? 200 : 420 }}
            title={item.name}
            onClick={() => actions.open(item)}
          >
            {item.name}
          </Typography.Link>
          {item.isStarred && <StarFilled style={{ color: '#faad14' }} />}
        </Space>
      ),
    },
    ...(extraColumns ?? []),
    ...(!isMobile
      ? [
          {
            title: t('drive.colLocation'),
            key: 'location',
            width: 200,
            ellipsis: true,
            render: (_: unknown, { item }: DriveRow<T>) => item.workspaceName ?? '—',
          },
        ]
      : []),
    {
      title: dateColumn?.title ?? t('drive.colModified'),
      key: 'date',
      width: 150,
      render: (_, row) => formatDateTime(dateColumn ? dateColumn.value(row) : (row.item.updatedAt ?? row.item.createdAt)),
    },
    ...(!isMobile
      ? [
          {
            title: t('drive.colSize'),
            key: 'size',
            width: 100,
            align: 'right' as const,
            render: (_: unknown, { item }: DriveRow<T>) => (item.type === 'folder' ? '—' : formatBytes(item.sizeBytes)),
          },
        ]
      : []),
    {
      key: 'actions',
      width: 48,
      render: (_, row) => (
        <FitDropdown menu={menuFor(row)}>
          <Button type="text" size="small" icon={<MoreOutlined />} aria-label={t('common.actions')} />
        </FitDropdown>
      ),
    },
  ]

  return (
    <>
      <Table<DriveRow<T>>
        rowKey="key"
        dataSource={rows}
        columns={columns}
        loading={loading}
        pagination={pagination}
        onChange={onChange}
        locale={{ emptyText: empty }}
        scroll={{ x: isMobile ? undefined : 760 }}
        onRow={(row) => ({
          // A click opens (like the explorer); buttons, links and the "..." menu keep their own action.
          onClick: (e) => {
            if (e.detail > 1 || (e.target as HTMLElement).closest('button, a, .ant-dropdown-trigger, .ant-checkbox-wrapper')) return
            actions.open(row.item)
          },
          onContextMenu: (e) => {
            e.preventDefault()
            setMenu({ row, pos: { x: e.clientX, y: e.clientY } })
          },
        })}
      />
      <PositionedMenu
        key={menu ? `${menu.pos.x},${menu.pos.y}` : 'none'}
        position={menu?.pos ?? null}
        onClose={() => setMenu(null)}
        menu={menu ? menuFor(menu.row) : { items: [] }}
      />
    </>
  )
}
