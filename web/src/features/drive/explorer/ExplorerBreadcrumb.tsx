import type { DragEvent } from 'react'
import { Breadcrumb, Skeleton } from 'antd'
import { HomeOutlined } from '@ant-design/icons'
import type { BreadcrumbItem } from '@/types/drive'

interface ExplorerBreadcrumbProps {
  crumbs: BreadcrumbItem[] | undefined
  onNavigate: (folderId: string | null) => void
  /** Drop dragged items on an ancestor folder to move them there. */
  onDropOn?: (folderId: string | null, e: DragEvent) => void
}

/** Path of the current folder (from folder-info); every ancestor is clickable and a drop target. */
export default function ExplorerBreadcrumb({ crumbs, onNavigate, onDropOn }: ExplorerBreadcrumbProps) {
  if (!crumbs) return <Skeleton.Input active size="small" style={{ width: 240 }} />
  return (
    <Breadcrumb
      className="bd-explorer-breadcrumb"
      items={crumbs.map((c, i) => {
        const last = i === crumbs.length - 1
        const label = (
          <span>
            {i === 0 && <HomeOutlined style={{ marginInlineEnd: 4 }} />}
            {c.name}
          </span>
        )
        return {
          key: c.id ?? 'root',
          title: last ? (
            <strong>{label}</strong>
          ) : (
            <a
              href="#"
              onClick={(e) => {
                e.preventDefault()
                onNavigate(c.id)
              }}
              onDragOver={(e) => {
                if (onDropOn) {
                  e.preventDefault()
                  e.dataTransfer.dropEffect = 'move'
                }
              }}
              onDrop={(e) => onDropOn?.(c.id, e)}
            >
              {label}
            </a>
          ),
        }
      })}
    />
  )
}
