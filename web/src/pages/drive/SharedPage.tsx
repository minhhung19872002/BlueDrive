import { Button, Card, DatePicker, Empty, Input, Select, Space, Switch, Tabs, Tag, Tooltip } from 'antd'
import { useQuery, keepPreviousData } from '@tanstack/react-query'
import type { Dayjs } from 'dayjs'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { qk } from '@/api/queryKeys'
import { listSharedWithMe, listSharers } from '@/api/sharing'
import PageHeader from '@/components/PageHeader'
import SharedAttachmentsList from '@/features/comments/SharedAttachmentsList'
import { useDriveActions } from '@/features/drive/actions/useDriveActions'
import DriveItemTable, { type DriveRow } from '@/features/drive/lists/DriveItemTable'
import { LEVEL_COLORS } from '@/features/sharing/levels'
import { useServerTable } from '@/hooks/useServerTable'
import { MEDIA_TYPES, type MediaType } from '@/types/drive'
import { accessRank, type SharedItem, type SharedWithMeFilter } from '@/types/sharing'

interface Filter extends SharedWithMeFilter {
  range?: [Dayjs, Dayjs] | null
}

/** "Được chia sẻ với tôi" (#19): filter by sharer, date range, media type and keyword. */
export default function SharedPage() {
  const { t } = useTranslation()
  const [search, setSearch] = useSearchParams()
  const tab = search.get('tab') === 'attachments' ? 'attachments' : 'items'
  const table = useServerTable<Filter>({})
  const { range, ...rest } = table.params
  const params = {
    ...rest,
    from: range?.[0]?.format('YYYY-MM-DD'),
    to: range?.[1]?.format('YYYY-MM-DD'),
  }
  const shared = useQuery({
    queryKey: [...qk.sharedWithMe, params],
    queryFn: () => listSharedWithMe(params),
    placeholderData: keepPreviousData,
  })
  const sharers = useQuery({ queryKey: qk.sharers, queryFn: listSharers, staleTime: 5 * 60_000 })
  const rows: DriveRow<SharedItem>[] = (shared.data?.items ?? []).map((s) => ({ key: s.shareId, item: s.item, extra: s }))
  const actions = useDriveActions({
    siblingFileIds: rows.filter((r) => r.item.type === 'file').map((r) => r.item.id),
  })
  const filtered = !!(table.filter.q || table.filter.sharedBy || table.filter.mediaType || table.filter.range)
  // Spec 1.3: grouped by who shared (the page's items).
  const [grouped, setGrouped] = useState(false)
  const groups = Object.entries(
    rows.reduce<Record<string, DriveRow<SharedItem>[]>>((acc, r) => {
      const name = r.extra?.sharedByName || t('drive.unknownSharer')
      ;(acc[name] ??= []).push(r)
      return acc
    }, {}),
  ).sort(([a], [b]) => a.localeCompare(b, 'vi'))

  return (
    <>
      <PageHeader title={t('menu.shared')} description={t('drive.sharedHint')} />
      <Tabs
        activeKey={tab}
        onChange={(k) => setSearch(k === 'attachments' ? { tab: k } : {}, { replace: true })}
        items={[
          { key: 'items', label: t('collab.attachments.tabShared') },
          { key: 'attachments', label: t('collab.attachments.tab') },
        ]}
      />
      {tab === 'attachments' ? (
        <Card styles={{ body: { padding: 0 } }}>
          <SharedAttachmentsList />
        </Card>
      ) : (
      <Card styles={{ body: { padding: 0 } }}>
        <Space wrap style={{ padding: 16 }}>
          <Input.Search
            allowClear
            placeholder={t('drive.sharedSearch')}
            onSearch={(q) => table.setFilter({ q: q.trim() || undefined })}
            style={{ width: 240 }}
          />
          <Select
            allowClear
            showSearch
            optionFilterProp="label"
            placeholder={t('drive.sharedBy')}
            value={table.filter.sharedBy}
            onChange={(v?: string) => table.setFilter({ sharedBy: v })}
            loading={sharers.isLoading}
            options={(sharers.data ?? []).map((u) => ({ value: u.id, label: `${u.fullName} (${u.username})` }))}
            style={{ width: 220 }}
          />
          <DatePicker.RangePicker
            format="DD/MM/YYYY"
            value={table.filter.range ?? null}
            onChange={(v) => table.setFilter({ range: v && v[0] && v[1] ? [v[0], v[1]] : null })}
            placeholder={[t('drive.fromDate'), t('drive.toDate')]}
          />
          <Select<MediaType | undefined>
            allowClear
            placeholder={t('drive.allTypes')}
            value={table.filter.mediaType}
            onChange={(v) => table.setFilter({ mediaType: v })}
            options={MEDIA_TYPES.map((m) => ({ value: m, label: t(`mediaType.${m}`) }))}
            style={{ width: 160 }}
          />
          {filtered && <Button onClick={table.resetFilter}>{t('common.clearFilter')}</Button>}
          <Space size={6}>
            <Switch size="small" checked={grouped} onChange={setGrouped} aria-label={t('drive.groupBySharer')} />
            <span>{t('drive.groupBySharer')}</span>
          </Space>
        </Space>
        {(grouped ? groups : [['', rows] as [string, DriveRow<SharedItem>[]]]).map(([sharer, groupRows]) => (
        <div key={sharer || 'all'}>
        {grouped && <div className="bd-shared-group" style={{ padding: '12px 16px 4px', fontWeight: 600 }}>{sharer} ({groupRows.length})</div>}
        <DriveItemTable<SharedItem>
          rows={groupRows}
          loading={shared.isFetching}
          actions={actions}
          flagsFor={(row) => {
            const rank = accessRank(row.extra!.accessLevel)
            return { canDownload: rank >= 2, canEdit: rank >= 3, canManage: rank >= 4 }
          }}
          extraColumns={[
            {
              title: t('drive.sharedBy'),
              key: 'sharedBy',
              width: 180,
              ellipsis: true,
              render: (_, row) =>
                row.extra?.message ? (
                  <Tooltip title={row.extra.message}>
                    <span>{row.extra.sharedByName}</span>
                  </Tooltip>
                ) : (
                  row.extra?.sharedByName
                ),
            },
            {
              title: t('sharing.level'),
              key: 'level',
              width: 150,
              render: (_, row) =>
                row.extra && <Tag color={LEVEL_COLORS[row.extra.accessLevel]}>{t(`sharing.levels.${row.extra.accessLevel}`)}</Tag>,
            },
          ]}
          dateColumn={{ title: t('drive.sharedAt'), value: (row) => row.extra?.sharedAt ?? null }}
          pagination={grouped ? false : table.pagination(shared.data?.total)}
          onChange={table.onTableChange}
          empty={<Empty description={filtered ? t('common.noMatch') : t('empty.shared')} style={{ padding: '32px 0' }} />}
        />
        </div>
        ))}
      </Card>
      )}
      {actions.dialogs}
    </>
  )
}
