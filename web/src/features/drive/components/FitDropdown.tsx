import { useRef, useState, type ReactNode } from 'react'
import { Dropdown, type MenuProps } from 'antd'
import { fitMenu } from '../explorer/fitMenu'

/**
 * "..." menu that always fits the window: opens below or above its button (whichever has more room) and scrolls inside when it
 * is taller than that room.
 */
export default function FitDropdown({ menu, align = 'right', children }: { menu: MenuProps; align?: 'left' | 'right'; children: ReactNode }) {
  const anchor = useRef<HTMLSpanElement>(null)
  const [fit, setFit] = useState<ReturnType<typeof fitMenu> | null>(null)
  const placement = fit ? (`${fit.placement.startsWith('top') ? 'top' : 'bottom'}${align === 'left' ? 'Left' : 'Right'}` as const) : undefined
  return (
    <Dropdown
      menu={{ ...menu, style: { ...menu.style, ...fit?.style } }}
      trigger={['click']}
      placement={placement ?? (align === 'left' ? 'bottomLeft' : 'bottomRight')}
      autoAdjustOverflow={false}
      onOpenChange={(open) => {
        const r = anchor.current?.getBoundingClientRect()
        if (open && r) setFit(fitMenu(r.top, r.bottom))
      }}
    >
      <span ref={anchor} style={{ display: 'inline-flex' }}>
        {children}
      </span>
    </Dropdown>
  )
}
