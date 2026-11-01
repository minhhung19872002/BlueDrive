import { Button, Checkbox, Dropdown, Spin, Tag, Typography } from 'antd'
import { MoreOutlined, StarFilled } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { formatBytes } from '@/utils/format'
import Thumbnail from '../components/Thumbnail'
import { APPROVAL_COLORS, itemKey } from '../utils'
import type { ItemViewProps } from './types'

const IGNORE = '.ant-checkbox-wrapper, button, .bd-row-actions'

/** Explorer grid view: thumbnail tiles (signed thumbnail URL for media, type icon otherwise). */
export default function ItemGridView(props: ItemViewProps) {
  const { items, loading, selected, dropTarget, empty } = props
  const { t } = useTranslation()
  if (!loading && items.length === 0) return <div className="bd-grid-empty">{empty}</div>
  return (
    <Spin spinning={loading}>
      <div className="bd-grid" role="list">
        {items.map((item) => {
          const key = itemKey(item)
          const isSelected = selected.has(key)
          return (
            <div
              key={key}
              role="listitem"
              className={['bd-tile', isSelected ? 'bd-tile-selected' : '', dropTarget === key ? 'bd-drop-target' : ''].join(' ')}
              {...props.dragProps(item)}
              onClick={(e) => {
                if ((e.target as HTMLElement).closest(IGNORE)) return
                props.onItemClick(item, e)
              }}
              onDoubleClick={(e) => {
                if ((e.target as HTMLElement).closest(IGNORE)) return
                props.onOpen(item)
              }}
              onContextMenu={(e) => props.onContextMenu(item, e)}
              title={item.name}
            >
              <Checkbox
                className="bd-tile-check"
                checked={isSelected}
                onChange={(e) => {
                  const next = new Set(selected)
                  if (e.target.checked) next.add(key)
                  else next.delete(key)
                  props.onSelectionChange([...next])
                }}
                aria-label={item.name}
              />
              <span className="bd-row-actions bd-tile-menu">
                <Dropdown menu={props.rowMenu(item)} trigger={['click']} placement="bottomRight">
                  <Button type="text" size="small" icon={<MoreOutlined />} aria-label={t('common.actions')} />
                </Dropdown>
              </span>
              <Thumbnail item={item} iconSize={item.type === 'folder' ? 56 : 48} />
              <div className="bd-tile-name">
                <Typography.Text ellipsis style={{ maxWidth: '100%' }}>
                  {item.isStarred && <StarFilled style={{ color: '#faad14', marginInlineEnd: 4 }} />}
                  {item.name}
                </Typography.Text>
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {item.type === 'folder' ? t('info.folder') : formatBytes(item.sizeBytes)}
                  {item.approvalStatus && item.approvalStatus !== 'approved' && (
                    <Tag color={APPROVAL_COLORS[item.approvalStatus]} style={{ marginInlineStart: 6, fontSize: 11 }}>
                      {t(`approval.status.${item.approvalStatus}`)}
                    </Tag>
                  )}
                </Typography.Text>
              </div>
            </div>
          )
        })}
      </div>
    </Spin>
  )
}
