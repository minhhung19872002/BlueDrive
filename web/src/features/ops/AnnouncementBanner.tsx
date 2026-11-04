import { useState } from 'react'
import { Alert, Space, Typography } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getActiveAnnouncements } from '@/api/operations'
import { qko } from '@/api/queryKeys.ops'
import type { Announcement, AnnouncementLevel } from '@/types/ops'
import { formatDateTime } from '@/utils/format'

const DISMISSED_KEY = 'bd.dismissedAnnouncements'

const ALERT_TYPE: Record<AnnouncementLevel, 'info' | 'warning' | 'error'> = { info: 'info', warning: 'warning', maintenance: 'error' }

/** A dismissal is tied to the announcement version, so an edited announcement shows again. */
const versionKey = (a: Announcement) => `${a.id}:${a.updatedAt ?? a.createdAt}`

function readDismissed(): string[] {
  try {
    const raw = localStorage.getItem(DISMISSED_KEY)
    const parsed: unknown = raw ? JSON.parse(raw) : []
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === 'string') : []
  } catch {
    return []
  }
}

function writeDismissed(keys: string[]): void {
  try {
    localStorage.setItem(DISMISSED_KEY, JSON.stringify(keys.slice(-50)))
  } catch {
    // Storage unavailable (private mode): the banner simply shows again next visit.
  }
}

/** Quality 5.2: system announcements (maintenance windows, upgrades, notices) shown on top of every page. */
export default function AnnouncementBanner() {
  const { t } = useTranslation()
  const [dismissed, setDismissed] = useState<string[]>(readDismissed)
  const { data } = useQuery({
    queryKey: qko.activeAnnouncements,
    queryFn: getActiveAnnouncements,
    // Realtime "announcement" events invalidate this query; polling covers start/end times and hub outages.
    refetchInterval: 5 * 60_000,
    staleTime: 60_000,
  })

  const visible = (data ?? []).filter((a) => !dismissed.includes(versionKey(a)))
  if (visible.length === 0) return null

  const dismiss = (a: Announcement) => {
    // Keep only keys of announcements still in effect so the list does not grow forever.
    const current = new Set((data ?? []).map(versionKey))
    const next = [...dismissed.filter((k) => current.has(k)), versionKey(a)]
    setDismissed(next)
    writeDismissed(next)
  }

  return (
    <Space direction="vertical" size={8} style={{ width: '100%', marginBottom: 16 }} className="no-print" role="region" aria-label={t('ops.announcement.region')}>
      {visible.map((a) => (
        <Alert
          key={a.id}
          type={ALERT_TYPE[a.level]}
          showIcon
          closable
          onClose={() => dismiss(a)}
          message={<Typography.Text strong>{a.title}</Typography.Text>}
          description={
            <>
              <div style={{ whiteSpace: 'pre-wrap' }}>{a.content}</div>
              {a.endsAt && (
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {t('ops.announcement.until', { time: formatDateTime(a.endsAt) })}
                </Typography.Text>
              )}
            </>
          }
        />
      ))}
    </Space>
  )
}
