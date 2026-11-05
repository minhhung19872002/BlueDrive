import { formatBytes } from '@/utils/format'

interface Entry {
  dataKey?: string | number | ((obj: unknown) => unknown)
  name?: string | number
  value?: number | string | readonly (number | string)[]
  color?: string
  payload?: Record<string, unknown>
}

interface Props {
  active?: boolean
  label?: string | number
  payload?: readonly Entry[]
}

/**
 * Tooltip of a 100% stacked bar whose rows keep raw bytes next to each percentage (`<key>Bytes`):
 * only the non-empty segments, largest first, so it stays short enough to fit beside the chart.
 */
export default function ShareTooltip({ active, label, payload }: Props) {
  if (!active || !payload?.length) return null
  const rows = payload
    .map((p) => ({
      key: String(p.dataKey),
      name: String(p.name ?? p.dataKey),
      color: p.color,
      percent: Number(p.value ?? 0),
      bytes: Number(p.payload?.[`${String(p.dataKey)}Bytes`] ?? 0),
    }))
    .filter((r) => r.percent > 0 || r.bytes > 0)
    .sort((a, b) => b.percent - a.percent)
  return (
    <div className="bd-chart-tooltip">
      <div className="bd-chart-tooltip-title">{label}</div>
      {rows.map((r) => (
        <div key={r.key} className="bd-chart-tooltip-row">
          <span className="bd-chart-tooltip-dot" style={{ background: r.color }} />
          <span className="bd-chart-tooltip-name">{r.name}</span>
          <span className="bd-chart-tooltip-value">
            {r.percent.toLocaleString('vi-VN')}% ({formatBytes(r.bytes)})
          </span>
        </div>
      ))}
    </div>
  )
}
