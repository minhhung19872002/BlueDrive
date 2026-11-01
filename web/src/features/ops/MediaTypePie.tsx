import { Empty } from 'antd'
import { Cell, Legend, Pie, PieChart, ResponsiveContainer, Tooltip } from 'recharts'
import { useTranslation } from 'react-i18next'
import type { TypeSlice } from '@/types/ops'
import { formatBytes } from '@/utils/format'
import { MEDIA_COLORS } from './utils'

interface MediaTypePieProps {
  slices: TypeSlice[]
  height?: number
}

/** Donut chart of storage by media type (#64, #69), one fixed color per type, tooltip with bytes + file count. */
export default function MediaTypePie({ slices, height = 280 }: MediaTypePieProps) {
  const { t } = useTranslation()
  const data = slices
    .filter((s) => s.bytes > 0)
    .map((s) => ({ ...s, label: t(`mediaType.${s.mediaType}`, { defaultValue: s.name }) }))
  const total = data.reduce((sum, s) => sum + s.bytes, 0)

  if (data.length === 0) {
    return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('dash.noFiles')} style={{ padding: '32px 0' }} />
  }

  return (
    <div style={{ width: '100%', height }} aria-label={t('dash.byTypeChart')} role="img">
      <ResponsiveContainer>
        <PieChart>
          <Pie data={data} dataKey="bytes" nameKey="label" innerRadius="45%" outerRadius="75%" paddingAngle={1} isAnimationActive={false}>
            {data.map((s) => (
              <Cell key={s.mediaType} fill={MEDIA_COLORS[s.mediaType]} stroke="#fff" />
            ))}
          </Pie>
          <Tooltip
            formatter={(value, name, item) => {
              const files = (item.payload as TypeSlice | undefined)?.files ?? 0
              const bytes = Number(value ?? 0)
              const pct = total > 0 ? ((bytes / total) * 100).toLocaleString('vi-VN', { maximumFractionDigits: 1 }) : '0'
              return [`${formatBytes(bytes)} (${pct}%) · ${t('dash.fileCount', { count: files })}`, name]
            }}
          />
          <Legend verticalAlign="bottom" iconType="circle" wrapperStyle={{ fontSize: 12 }} />
        </PieChart>
      </ResponsiveContainer>
    </div>
  )
}
