import { Button, Table, Tag, Tooltip, Typography, type TableProps } from 'antd'
import { DownloadOutlined, LinkOutlined, LockOutlined, MoreOutlined, ShareAltOutlined, StarFilled } from '@ant-design/icons'
import type { SorterResult, SortOrder } from 'antd/es/table/interface'
import type { Breakpoint } from 'antd/es/_util/responsiveObserver'
import { useTranslation } from 'react-i18next'
import type { DriveItem } from '@/types/drive'
import { formatBytes, formatDateTime } from '@/utils/format'
import FileTypeIcon from '../components/FileTypeIcon'
import ShortcutTag from '../components/ShortcutTag'
import { APPROVAL_COLORS, itemKey } from '../utils'
import type { ItemViewProps } from './types'
import FitDropdown from '../components/FitDropdown'

interface ItemListViewProps extends ItemViewProps {
  sort: string | undefined
  onSortChange: (sort: string | undefined) => void
  /** The info panel takes room next to the list: show fewer columns before scrolling. */
  infoOpen?: boolean
}

const SORTABLE = ['name', 'size', 'updatedAt']

/** Quick actions shown next to the name while the row is hovered (only those the item's menu offers). */
const QUICK_ACTIONS = [
  { key: 'share', icon: <ShareAltOutlined />, label: 'actions.share' },
  { key: 'copyLink', icon: <LinkOutlined />, label: 'actions.copyLink' },
  { key: 'download', icon: <DownloadOutlined />, label: 'actions.download' },
] as const

/** Clicks on these elements must not change the selection (checkbox cell, action buttons). */
const IGNORE_CLICK = '.ant-table-selection-column, .ant-checkbox-wrapper, button, a, .bd-row-actions'

/** Explorer list view: antd Table with server sorting by name / size / updatedAt. */
export default function ItemListView(props: ItemListViewProps) {
  const { items, loading, selected, dropTarget, isMobile, sort, onSortChange, empty } = props
  const { t } = useTranslation()
  const order = (key: string): SortOrder => (sort === key ? 'ascend' : sort === `-${key}` ? 'descend' : null)

  const columns: TableProps<DriveItem>['columns'] = [
    {
      title: t('drive.colName'),
      key: 'name',
      sorter: true,
      sortOrder: order('name'),
      ellipsis: true,
      render: (_, item) => {
        const menu = props.rowMenu(item)
        const offered = new Set((menu.items ?? []).map((m) => (m && 'key' in m ? String(m.key) : '')))
        return (
          <div className="bd-name-cell">
            <FileTypeIcon type={item.type} mediaType={item.mediaType} extension={item.extension} size={24} />
            <Typography.Text ellipsis className="bd-item-name" title={item.name}>
              {item.name}
            </Typography.Text>
            <ShortcutTag item={item} />
            {item.checkedOutBy && (
              <Tooltip title={t('library.checkedOutTag')}>
                <LockOutlined style={{ color: '#fa8c16', flex: 'none' }} aria-label={t('library.checkedOutTag')} />
              </Tooltip>
            )}
            {item.isStarred && <StarFilled style={{ color: '#faad14', flex: 'none' }} />}
            {item.approvalStatus && item.approvalStatus !== 'approved' && (
              <Tag color={APPROVAL_COLORS[item.approvalStatus]} style={{ flex: 'none', marginInlineEnd: 0 }}>
                {t(`approval.status.${item.approvalStatus}`)}
              </Tag>
            )}
            {!isMobile && (
              <span className="bd-row-actions bd-quick-actions">
                <FitDropdown menu={menu} align="left">
                  <Button type="text" size="small" icon={<MoreOutlined />} aria-label={t('actions.more')} title={t('actions.more')} />
                </FitDropdown>
                {QUICK_ACTIONS.filter((a) => offered.has(a.key)).map((a) => (
                  <Button
                    key={a.key}
                    type="text"
                    size="small"
                    icon={a.icon}
                    aria-label={t(a.label)}
                    title={t(a.label)}
                    onClick={(e) => {
                      e.stopPropagation()
                      props.runAction(a.key, item)
                    }}
                  />
                ))}
              </span>
            )}
          </div>
        )
      },
    },
    ...(!isMobile
      ? [
          {
            title: t('drive.colOwner'),
            key: 'owner',
            width: 180,
            // Responsive: dropped on narrower windows (and earlier when the info panel, which shows the owner, is open).
            responsive: props.infoOpen ? (['xxl'] as Breakpoint[]) : (['xl'] as Breakpoint[]),
            ellipsis: true,
            render: (_: unknown, item: DriveItem) => item.createdByName ?? '—',
          },
        ]
      : []),
    {
      title: t('drive.colModified'),
      key: 'updatedAt',
      width: isMobile ? 120 : 160,
      sorter: true,
      sortOrder: order('updatedAt'),
      render: (_, item) => formatDateTime(item.updatedAt ?? item.createdAt),
    },
    ...(!isMobile
      ? [
          {
            title: t('drive.colSize'),
            key: 'size',
            width: 110,
            sorter: true,
            sortOrder: order('size'),
            align: 'right' as const,
            render: (_: unknown, item: DriveItem) => (item.type === 'folder' ? '—' : formatBytes(item.sizeBytes)),
          },
        ]
      : []),
    // Phones have no hover: they keep a trailing "..." column (desktop shows it next to the name on hover).
    ...(isMobile
      ? [
          {
            key: 'actions',
            width: 48,
            render: (_: unknown, item: DriveItem) => (
              <span className="bd-row-actions">
                <FitDropdown menu={props.rowMenu(item)}>
                  <Button type="text" size="small" icon={<MoreOutlined />} aria-label={t('common.actions')} />
                </FitDropdown>
              </span>
            ),
          },
        ]
      : []),
  ]

  return (
    <Table<DriveItem>
      rowKey={itemKey}
      size="middle"
      className="bd-explorer-table"
      dataSource={items}
      columns={columns}
      loading={loading}
      pagination={false}
      locale={{ emptyText: empty }}
      rowSelection={{
        selectedRowKeys: [...selected],
        onChange: (keys) => props.onSelectionChange(keys as string[]),
        columnWidth: 40,
      }}
      rowClassName={(item) => {
        const key = itemKey(item)
        return [selected.has(key) ? 'bd-row-selected' : '', dropTarget === key ? 'bd-drop-target' : ''].join(' ')
      }}
      onChange={(_p, _f, sorter) => {
        const s = (Array.isArray(sorter) ? sorter[0] : sorter) as SorterResult<DriveItem>
        const key = String(s?.columnKey ?? '')
        onSortChange(s?.order && SORTABLE.includes(key) ? `${s.order === 'descend' ? '-' : ''}${key}` : undefined)
      }}
      onRow={(item) => ({
        ...props.dragProps(item),
        onClick: (e) => {
          // Clicks inside portaled menus bubble through React to the row: ignore them.
          if (!e.currentTarget.contains(e.target as Node) || (e.target as HTMLElement).closest(IGNORE_CLICK)) return
          props.onItemClick(item, e)
        },
        onContextMenu: (e) => props.onContextMenu(item, e),
      })}
      // Minimum width: only small windows scroll horizontally, the name column never gets squeezed.
      scroll={{ x: isMobile ? undefined : 600 }}
      // Header row and horizontal scrollbar stay on screen (below the app header, and below the selection command bar that
      // sticks while items are selected: 32px buttons + 2 × 8px padding; at the bottom of the window).
      sticky={isMobile ? undefined : { offsetHeader: selected.size > 0 ? 64 + 48 : 64 }}
    />
  )
}
