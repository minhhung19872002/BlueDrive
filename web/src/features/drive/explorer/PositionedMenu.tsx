import { Dropdown, type MenuProps } from 'antd'

export interface MenuPosition {
  x: number
  y: number
}

/** Context menu opened at the mouse position (one instance per listing instead of one per row). */
export default function PositionedMenu({
  position,
  menu,
  onClose,
}: {
  position: MenuPosition | null
  menu: MenuProps
  onClose: () => void
}) {
  if (!position) return null
  return (
    <Dropdown
      open
      menu={{
        ...menu,
        onClick: (info) => {
          onClose()
          menu.onClick?.(info)
        },
      }}
      trigger={['contextMenu']}
      onOpenChange={(open) => !open && onClose()}
      destroyOnHidden
    >
      <div
        aria-hidden
        style={{ position: 'fixed', left: position.x, top: position.y, width: 1, height: 1, pointerEvents: 'none' }}
      />
    </Dropdown>
  )
}
