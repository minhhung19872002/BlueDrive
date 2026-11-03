import { Button, Dropdown, Space, Table, Tag, Typography, type TableProps } from 'antd'
import { MoreOutlined, StarFilled } from '@ant-design/icons'
import type { SorterResult, SortOrder } from 'antd/es/table/interface'
import { useTranslation } from 'react-i18next'
import type { DriveItem } from '@/types/drive'
import { formatBytes, formatDateTime } from '@/utils/format'
import FileTypeIcon from '../components/FileTypeIcon'
import { APPROVAL_COLORS, itemKey } from '../utils'
import type { ItemViewProps } from './types'

interface ItemListViewProps extends ItemViewProps {
  sort: string | undefined
  onSortChange: (sort: string | undefined) => void
}

const SORTABLE = ['name', 'size', 'updatedAt']

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
      render: (_, item) => (
        <Space size={10} style={{ minWidth: 0, maxWidth: '100%' }}>
          <FileTypeIcon type={item.type} mediaType={item.mediaType} extension={item.extension} size={22} />
          <Typography.Text ellipsis style={{ maxWidth: isMobile ? 180 : 420 }} title={item.name}>
            {item.name}
          </Typography.Text>
          {item.isStarred && <StarFilled style={{ color: '#faad14' }} />}
          {item.approvalStatus && item.approvalStatus !== 'approved' && (
            <Tag color={APPROVAL_COLORS[item.approvalStatus]}>{t(`approval.status.${item.approvalStatus}`)}</Tag>
          )}
        </Space>
      ),
    },
    ...(!isMobile
      ? [
          {
            title: t('drive.colOwner'),
            key: 'owner',
            width: 180,
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
    {
      key: 'actions',
      width: 48,
      render: (_, item) => (
        <span className="bd-row-actions">
          <Dropdown menu={props.rowMenu(item)} trigger={['click']} placement="bottomRight">
            <Button type="text" size="small" icon={<MoreOutlined />} aria-label={t('common.actions')} />
          </Dropdown>
        </span>
      ),
    },
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
          if ((e.target as HTMLElement).closest(IGNORE_CLICK)) return
          props.onItemClick(item, e)
        },
        onContextMenu: (e) => props.onContextMenu(item, e),
      })}
      scroll={{ x: isMobile ? undefined : 640 }}
    />
  )
}
