import { Dropdown, type MenuProps } from 'antd'
import { fitMenu } from './fitMenu'

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
  const fit = fitMenu(position.y)
  return (
    <Dropdown
      open
      placement={fit.placement}
      autoAdjustOverflow={false}
      menu={{
        ...menu,
        style: { ...menu.style, ...fit.style },
        onClick: (info) => {
          onClose()
          menu.onClick?.(info)
        },
      }}
      // Not "contextMenu": antd then aligns to the pointer (alignPoint) and rc-trigger closes such popups on
      // any scroll, e.g. when the page height changes right after the right-click. The anchor is already at
      // the pointer; "click" keeps closing on a click / right-click outside.
      trigger={['click']}
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
