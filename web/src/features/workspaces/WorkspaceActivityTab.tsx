import { useState } from 'react'
import { Button, Card, DatePicker, Empty, Grid, Pagination, Result, Select, Space, Spin, Timeline, Typography } from 'antd'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import type { Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { getErrorMessage } from '@/api/errors'
import { getWorkspaceActivity } from '@/api/insights'
import { qk } from '@/api/queryKeys'
import { qkc } from '@/api/queryKeys.collab'
import { listWorkspaceMembers } from '@/api/workspaces'
import { ACTIVITY_ACTIONS, type ActivityDto, type AuditAction } from '@/types/collab'
import { formatDateTime } from '@/utils/format'
import '@/styles/collab.css'

const PAGE_SIZE = 30

const ACTION_COLORS: Partial<Record<AuditAction, string>> = {
  upload: 'green',
  create: 'green',
  edit: 'blue',
  rename: 'blue',
  move: 'blue',
  copy: 'blue',
  version_restore: 'purple',
  restore: 'purple',
  comment: 'cyan',
  share: 'cyan',
  unshare: 'gray',
  approve: 'green',
  reject: 'red',
  delete: 'red',
  purge: 'red',
  permission_change: 'orange',
}

/** Actions after which the resource no longer exists in place (no link). */
const GONE: AuditAction[] = ['delete', 'purge']

function resourceLink(a: ActivityDto, workspaceId: string): string | null {
  if (!a.resourceId || GONE.includes(a.action)) return null
  if (a.resourceType === 'file') return a.action === 'comment' ? `/files/${a.resourceId}?tab=discussion` : `/files/${a.resourceId}`
  if (a.resourceType === 'folder') return `/workspaces/${workspaceId}?folder=${a.resourceId}`
  return null
}

/** "Lịch sử chỉnh sửa" of a group workspace (#31): who changed what, filterable by member, action and date. */
export default function WorkspaceActivityTab({ workspaceId }: { workspaceId: string }) {
  const { t } = useTranslation()
  const screens = Grid.useBreakpoint()
  const isMobile = screens.md === false
  const [userId, setUserId] = useState<string | undefined>()
  const [action, setAction] = useState<AuditAction | undefined>()
  const [range, setRange] = useState<[Dayjs, Dayjs] | null>(null)
  const [page, setPage] = useState(1)

  const params = {
    userId,
    action,
    from: range?.[0].format('YYYY-MM-DD'),
    to: range?.[1].format('YYYY-MM-DD'),
    page,
    pageSize: PAGE_SIZE,
  }
  const activity = useQuery({
    queryKey: qkc.workspaceActivity(workspaceId, params),
    queryFn: () => getWorkspaceActivity(workspaceId, params),
    placeholderData: keepPreviousData,
  })
  const members = useQuery({ queryKey: qk.workspaceMembers(workspaceId), queryFn: () => listWorkspaceMembers(workspaceId), retry: 0 })
  const filtered = !!(userId || action || range)

  const reset = <T,>(setter: (v: T) => void) => (v: T) => {
    setter(v)
    setPage(1)
  }

  if (activity.isError) return <Result status="warning" subTitle={getErrorMessage(activity.error)} />
  const items = activity.data?.items ?? []

  return (
    <Card size="small" styles={{ body: { padding: isMobile ? 12 : 16 } }}>
      <Space wrap style={{ marginBottom: 16 }}>
        {/* The member list is for who administers the group: others get no member filter. */}
        {!members.isError && (
          <Select
            allowClear
            showSearch
            optionFilterProp="label"
            placeholder={t('collab.activity.allMembers')}
            value={userId}
            onChange={reset(setUserId)}
            loading={members.isLoading}
            options={(members.data ?? []).map((m) => ({ value: m.userId, label: `${m.fullName} (${m.username})` }))}
            style={{ width: isMobile ? '100%' : 240 }}
          />
        )}
        <Select<AuditAction | undefined>
          allowClear
          placeholder={t('collab.activity.allActions')}
          value={action}
          onChange={reset(setAction)}
          options={ACTIVITY_ACTIONS.map((a) => ({ value: a, label: t(`collab.activity.actions.${a}`) }))}
          style={{ width: 200 }}
        />
        <DatePicker.RangePicker
          format="DD/MM/YYYY"
          value={range}
          onChange={(v) => reset(setRange)(v && v[0] && v[1] ? [v[0], v[1]] : null)}
          placeholder={[t('drive.fromDate'), t('drive.toDate')]}
        />
        {filtered && (
          <Button
            onClick={() => {
              setUserId(undefined)
              setAction(undefined)
              setRange(null)
              setPage(1)
            }}
          >
            {t('common.clearFilter')}
          </Button>
        )}
      </Space>

      <Spin spinning={activity.isFetching}>
        {items.length === 0 && !activity.isLoading ? (
          <Empty description={filtered ? t('common.noMatch') : t('collab.activity.empty')} style={{ padding: '32px 0' }} />
        ) : (
          <Timeline
            className="bd-activity"
            items={items.map((a) => {
              const link = resourceLink(a, workspaceId)
              const who = a.fullName || a.username || t('collab.activity.system')
              const name = a.resourceName || '—'
              return {
                key: a.id,
                color: ACTION_COLORS[a.action] ?? 'gray',
                children: (
                  <div>
                    <Typography.Text strong>{who}</Typography.Text>{' '}
                    <Typography.Text>{t(`collab.activity.actions.${a.action}`, { defaultValue: a.action }).toLowerCase()}</Typography.Text>{' '}
                    {link ? <Link to={link}>{name}</Link> : <Typography.Text italic>{name}</Typography.Text>}
                    <div>
                      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                        {formatDateTime(a.occurredAt)}
                        {a.resourceType && ` · ${t(`collab.activity.resource.${a.resourceType}`, { defaultValue: a.resourceType })}`}
                      </Typography.Text>
                    </div>
                  </div>
                ),
              }
            })}
          />
        )}
      </Spin>
      {(activity.data?.total ?? 0) > PAGE_SIZE && (
        <Pagination
          current={page}
          pageSize={PAGE_SIZE}
          total={activity.data?.total ?? 0}
          showSizeChanger={false}
          size={isMobile ? 'small' : 'default'}
          showTotal={(count) => t('common.totalItems', { count })}
          onChange={setPage}
          style={{ textAlign: 'right' }}
        />
      )}
    </Card>
  )
}
