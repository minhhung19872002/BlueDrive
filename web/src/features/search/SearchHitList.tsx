import type { ReactNode } from 'react'
import { Button, Dropdown, Empty, Space, Tag, Typography, type MenuProps } from 'antd'
import { ApartmentOutlined, CalendarOutlined, FolderOutlined, MoreOutlined, UserOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import type { DriveActions } from '@/features/drive/actions/useDriveActions'
import Thumbnail from '@/features/drive/components/Thumbnail'
import type { SearchHit } from '@/types/collab'
import { formatBytes, formatDateTime } from '@/utils/format'
import HighlightedText from './HighlightedText'
import '@/styles/collab.css'

interface SearchHitListProps {
  hits: SearchHit[]
  actions: DriveActions
  empty?: ReactNode
}

/** Search results: thumbnail, highlighted name / title, snippet, location, uploader unit, size and date. */
export default function SearchHitList({ hits, actions, empty }: SearchHitListProps) {
  const { t } = useTranslation()
  if (hits.length === 0) return <>{empty ?? <Empty description={t('common.noMatch')} style={{ padding: '32px 0' }} />}</>
  return (
    <div role="list">
      {hits.map((h) => {
        const item = h.item
        const showTitle = !!item.title && item.title !== item.name
        const menu: MenuProps = {
          items: actions.menuItems([item]),
          onClick: ({ key, domEvent }) => {
            domEvent.stopPropagation()
            actions.run(key, [item])
          },
        }
        return (
          <div
            key={item.id}
            role="listitem"
            className="bd-search-hit"
            tabIndex={0}
            onClick={() => actions.open(item)}
            onKeyDown={(e) => e.key === 'Enter' && actions.open(item)}
          >
            <div className="bd-search-thumb">
              <Thumbnail item={item} iconSize={32} />
            </div>
            <div className="bd-search-hit-body">
              <Typography.Link className="bd-search-hit-name">
                <HighlightedText html={h.highlightedName} fallback={item.name} />
              </Typography.Link>
              {showTitle && (
                <Typography.Text style={{ display: 'block' }}>
                  <HighlightedText html={h.highlightedTitle} fallback={item.title} />
                </Typography.Text>
              )}
              {h.snippet && (
                <span className="bd-search-snippet">
                  <HighlightedText html={h.snippet} />
                </span>
              )}
              <div className="bd-search-meta">
                {item.mediaType && <Tag style={{ marginInlineEnd: 0 }}>{t(`mediaType.${item.mediaType}`)}</Tag>}
                <span title={h.location}>
                  <FolderOutlined /> {h.location}
                </span>
                {item.createdByName && (
                  <span>
                    <UserOutlined /> {item.createdByName}
                  </span>
                )}
                {h.uploaderOrganization && (
                  <span>
                    <ApartmentOutlined /> {h.uploaderOrganization}
                  </span>
                )}
                <span>{formatBytes(item.sizeBytes)}</span>
                <span>
                  <CalendarOutlined /> {formatDateTime(item.createdAt)}
                </span>
              </div>
            </div>
            <Space onClick={(e) => e.stopPropagation()} style={{ alignSelf: 'flex-start' }}>
              <Dropdown menu={menu} trigger={['click']} placement="bottomRight">
                <Button type="text" size="small" icon={<MoreOutlined />} aria-label={t('common.actions')} />
              </Dropdown>
            </Space>
          </div>
        )
      })}
    </div>
  )
}
