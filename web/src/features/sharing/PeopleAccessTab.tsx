import { useEffect, useMemo, useState } from 'react'
import {
  Alert,
  Button,
  Checkbox,
  DatePicker,
  Divider,
  Dropdown,
  Empty,
  Input,
  List,
  Select,
  Skeleton,
  Space,
  Tag,
  Tooltip,
  Typography,
} from 'antd'
import { ApartmentOutlined, DeleteOutlined, GlobalOutlined, LockOutlined, SaveOutlined, ShareAltOutlined, TeamOutlined, UndoOutlined, UserOutlined } from '@ant-design/icons'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import dayjs, { type Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'
import {
  breakInheritance,
  getItemPermissions,
  getItemSharingPolicy,
  grantItemPermission,
  listPermissionLevels,
  restoreInheritance,
  revokeItemPermission,
  setGeneralAccess,
  updateItemPermission,
  type ItemPermission,
  type PrincipalKind,
  type PrincipalTarget,
} from '@/api/permissions'
import { useApiMutation } from '@/hooks/useApiMutation'
import { formatDateTime } from '@/utils/format'
import { splitPrincipal, type PrincipalValue } from './levels'
import PrincipalSelect from './PrincipalSelect'
import { usePublicLink } from './usePublicLink'

type GeneralMode = 'restricted' | 'organization' | 'anyone'

const ORIGIN_COLORS: Record<ItemPermission['origin'], string> = { direct: 'green', inherited: 'purple', space: 'blue' }

function KindIcon({ kind }: { kind: PrincipalKind }) {
  switch (kind) {
    case 'group':
    case 'space_group':
      return <TeamOutlined />
    case 'org_unit':
    case 'org_unit_tree':
      return <ApartmentOutlined />
    case 'everyone':
      return <GlobalOutlined />
    default:
      return <UserOutlined />
  }
}

function toTarget(value: PrincipalValue): PrincipalTarget {
  const { type, id } = splitPrincipal(value.value)
  // A unit is shared with its sub-units too (as everywhere else in BlueDrive).
  return { kind: type === 'organization' ? 'org_unit_tree' : type === 'space' ? 'space_group' : (type as PrincipalKind), id }
}

/**
 * Spec 5.1 (Google Drive / SharePoint): add people, groups or units with a permission level, an optional end date and a note;
 * "Người có quyền truy cập" with where each access comes from; "Quyền truy cập chung"; stop / restore inheritance.
 */
/** An unsaved change of a row: a new level, a new expiry (null = no expiry), or removal. */
interface Draft {
  levelId?: string
  expiresAt?: string | null
  remove?: boolean
}

/** @param onDirtyChange Told whether rows have unsaved changes, so the dialog can ask before closing. */
export default function PeopleAccessTab({ itemId, onDirtyChange }: { itemId: string; onDirtyChange?: (dirty: boolean) => void }) {
  const { t } = useTranslation()
  const key = ['item-permissions', itemId]
  const access = useQuery({ queryKey: key, queryFn: () => getItemPermissions(itemId) })
  const levels = useQuery({ queryKey: ['permission-levels'], queryFn: listPermissionLevels, staleTime: 5 * 60_000 })
  const offered = useMemo(() => (levels.data ?? []).filter((l) => l.code !== 'owner'), [levels.data])
  const defaultLevel = offered.find((l) => l.code === 'viewer')?.id

  const [targets, setTargets] = useState<PrincipalValue[]>([])
  const [emails, setEmails] = useState<string[]>([])
  const [levelId, setLevelId] = useState<string | undefined>()
  const [expires, setExpires] = useState<Dayjs | null>(null)
  const [notify, setNotify] = useState(true)
  const [message, setMessage] = useState('')

  const opts = { invalidate: [key, ['shares']] }
  const grant = useApiMutation(
    () =>
      grantItemPermission(itemId, {
        targets: targets.map(toTarget),
        emails,
        levelId: (levelId ?? defaultLevel)!,
        expiresAt: expires ? expires.endOf('day').toISOString() : null,
        notify,
        message: message.trim() || null,
      }),
    {
      ...opts,
      successMessage: t('sharing.people.shared'),
      onSuccess: () => {
        setTargets([])
        setEmails([])
        setExpires(null)
        setMessage('')
      },
    },
  )
  // Level, expiry and removal of the rows are kept here and sent together with "Lưu" (like Google Drive), not on every pick.
  const [drafts, setDrafts] = useState<Record<string, Draft>>({})
  const edit = (entryId: string, d: Draft) => setDrafts((p) => ({ ...p, [entryId]: { ...p[entryId], ...d } }))
  const queryClient = useQueryClient()
  const save = useApiMutation(
    async (list: { id: string; draft: Draft }[]) => {
      // One by one: what was applied leaves the drafts at once, so a failure part way keeps only what is still to do.
      for (const { id, draft } of list) {
        if (draft.remove) await revokeItemPermission(itemId, id)
        else
          await updateItemPermission(itemId, id, {
            levelId: draft.levelId,
            expiresAt: draft.expiresAt ?? undefined,
            clearExpiry: draft.expiresAt === null,
          })
        setDrafts((p) => {
          const next = { ...p }
          delete next[id]
          return next
        })
      }
    },
    {
      ...opts,
      successMessage: t('sharing.people.saved'),
      onError: () => {
        void queryClient.invalidateQueries({ queryKey: key })
      },
    },
  )
  const general = useApiMutation((v: { mode: 'restricted' | 'organization'; levelId: string | null }) => setGeneralAccess(itemId, v.mode, v.levelId), {
    ...opts,
    successMessage: t('sharing.people.generalSaved'),
  })
  const stop = useApiMutation((copy: boolean) => breakInheritance(itemId, copy), { ...opts, successMessage: t('sharing.people.stopped') })
  const restore = useApiMutation((recursive: boolean) => restoreInheritance(itemId, recursive), { ...opts, successMessage: t('sharing.people.restored') })
  const policy = useQuery({ queryKey: ['item-sharing-policy', itemId], queryFn: () => getItemSharingPolicy(itemId) })
  const kind = access.data?.kind
  const publicLink = usePublicLink(kind === 'file' || kind === 'folder' ? kind : null, itemId)

  // Only what differs from the server counts (picking the old level again is no change).
  const changes = (access.data?.entries ?? []).flatMap((e) => {
    const d = drafts[e.id]
    if (!d || !e.editable) return []
    const draft: Draft = d.remove
      ? { remove: true }
      : {
          ...(d.levelId !== undefined && d.levelId !== e.levelId ? { levelId: d.levelId } : {}),
          ...(d.expiresAt !== undefined && d.expiresAt !== (e.expiresAt ?? null) ? { expiresAt: d.expiresAt } : {}),
        }
    return Object.keys(draft).length ? [{ id: e.id, draft }] : []
  })
  const dirty = changes.length > 0
  useEffect(() => onDirtyChange?.(dirty), [dirty, onDirtyChange])

  if (access.isLoading || levels.isLoading) return <Skeleton active />
  if (!access.data) return <Empty />
  const a = access.data
  // "Duyệt nội dung" only where the library approves content; a level someone already has stays selectable on their row.
  const toOption = (l: (typeof offered)[number]) => ({ value: l.id, label: l.name, title: l.description })
  const levelOptions = offered.filter((l) => a.approvalEnabled || l.code !== 'approver').map(toOption)
  const optionsWith = (current?: string | null) => {
    const extra = offered.find((l) => l.id === current && !levelOptions.some((o) => o.value === l.id))
    return extra ? [...levelOptions, toOption(extra)] : levelOptions
  }
  const isRoot = a.kind === 'space' || a.kind === 'drive'
  const mode: GeneralMode = publicLink.on ? 'anyone' : a.generalAccess

  return (
    <>
      {a.canShare && (
        <Space direction="vertical" style={{ width: '100%' }}>
          <PrincipalSelect multiple types={['user', 'space', 'group', 'organization']} value={targets} onChange={setTargets} placeholder={t('sharing.people.addPlaceholder')} />
          {(policy.data?.external ?? 'off') !== 'off' && (
            <Select
              mode="tags"
              style={{ width: '100%' }}
              value={emails}
              onChange={(v: string[]) => setEmails(v.map((x) => x.trim()).filter(Boolean))}
              tokenSeparators={[',', ';', ' ']}
              open={false}
              suffixIcon={null}
              placeholder={t('sharing.people.emailPlaceholder')}
              aria-label={t('sharing.people.emailPlaceholder')}
            />
          )}
          {(targets.length > 0 || emails.length > 0) && (
            <>
              <Space wrap style={{ width: '100%' }}>
                <Select style={{ minWidth: 220 }} value={levelId ?? defaultLevel} onChange={setLevelId} options={levelOptions} aria-label={t('sharing.people.level')} />
                <DatePicker
                  value={expires}
                  onChange={setExpires}
                  format="DD/MM/YYYY"
                  placeholder={t('sharing.people.noExpiry')}
                  disabledDate={(d) => d.isBefore(dayjs(), 'day')}
                  aria-label={t('sharing.people.expiresAt')}
                />
              </Space>
              <Checkbox checked={notify} onChange={(e) => setNotify(e.target.checked)}>
                {t('sharing.people.notify')}
              </Checkbox>
              {notify && (
                <Input.TextArea rows={2} maxLength={1000} value={message} onChange={(e) => setMessage(e.target.value)} placeholder={t('sharing.people.messagePlaceholder')} aria-label={t('sharing.message')} />
              )}
              <Button type="primary" icon={<ShareAltOutlined />} loading={grant.isPending} onClick={() => grant.mutate(undefined)}>
                {t('sharing.people.share')}
              </Button>
            </>
          )}
        </Space>
      )}

      <Divider orientation="left" orientationMargin={0}>
        {t('sharing.people.withAccess')}
      </Divider>
      <List
        size="small"
        dataSource={a.entries.filter((e) => e.principalKind !== 'link') /* the link is shown below, not as a person */}
        locale={{ emptyText: t('sharing.people.nobody') }}
        renderItem={(e) => {
          const d = drafts[e.id] ?? {}
          const removing = !!d.remove
          const shownLevel = d.levelId ?? e.levelId
          const shownExpiry = d.expiresAt !== undefined ? d.expiresAt : e.expiresAt
          return (
          <List.Item
            style={removing ? { opacity: 0.55 } : undefined}
            actions={
              e.editable
                ? [
                    removing ? (
                      <Tooltip key="undo" title={t('sharing.people.undoRemove')}>
                        <Button size="small" type="text" icon={<UndoOutlined />} aria-label={t('sharing.people.undoRemove')} onClick={() => edit(e.id, { remove: false })} />
                      </Tooltip>
                    ) : (
                      <Tooltip key="del" title={t('sharing.people.remove')}>
                        <Button size="small" type="text" danger icon={<DeleteOutlined />} aria-label={t('sharing.people.remove')} onClick={() => edit(e.id, { remove: true })} />
                      </Tooltip>
                    ),
                  ]
                : []
            }
          >
            <List.Item.Meta
              avatar={<KindIcon kind={e.principalKind} />}
              title={
                <Space size={4} wrap>
                  <span style={removing ? { textDecoration: 'line-through' } : undefined}>{e.principalName}</span>
                  {removing && <Tag color="red">{t('sharing.people.willRemove')}</Tag>}
                  {/* Space membership needs no badge: the row already reads "Nhóm của Space". */}
                  {e.origin !== 'space' && <Tag color={ORIGIN_COLORS[e.origin]}>{e.originLabel}</Tag>}
                  {!e.appliesToChildren && <Tag>{t('sharing.people.thisItemOnly')}</Tag>}
                </Space>
              }
              description={
                <Space size={8} wrap>
                  {e.detail && <Typography.Text type="secondary">{e.detail}</Typography.Text>}
                  {e.editable ? (
                    <>
                      <Select
                        size="small"
                        style={{ minWidth: 180 }}
                        value={shownLevel}
                        options={optionsWith(e.levelId)}
                        disabled={removing}
                        onChange={(v) => edit(e.id, { levelId: v })}
                        aria-label={t('sharing.people.level')}
                      />
                      <DatePicker
                        size="small"
                        value={shownExpiry ? dayjs(shownExpiry) : null}
                        disabled={removing}
                        format="DD/MM/YYYY"
                        placeholder={t('sharing.people.noExpiry')}
                        disabledDate={(d) => d.isBefore(dayjs(), 'day')}
                        onChange={(day) => edit(e.id, { expiresAt: day ? day.endOf('day').toISOString() : null })}
                        aria-label={t('sharing.people.expiresAt')}
                      />
                    </>
                  ) : (
                    <>
                      <Tag>{e.levelName}</Tag>
                      {e.expiresAt && <Typography.Text type="secondary">{t('sharing.people.until', { date: formatDateTime(e.expiresAt) })}</Typography.Text>}
                    </>
                  )}
                </Space>
              }
            />
          </List.Item>
          )
        }}
      />
      {dirty && (
        <Alert
          type="warning"
          showIcon
          style={{ marginTop: 8 }}
          message={t('sharing.people.unsaved', { count: changes.length })}
          action={
            <Space>
              <Button size="small" disabled={save.isPending} onClick={() => setDrafts({})}>
                {t('sharing.people.discard')}
              </Button>
              <Button size="small" type="primary" icon={<SaveOutlined />} loading={save.isPending} onClick={() => save.mutate(changes)}>
                {t('sharing.people.save')}
              </Button>
            </Space>
          }
        />
      )}

      <Divider orientation="left" orientationMargin={0}>
        {t('sharing.people.generalAccess')}
      </Divider>
      <Space wrap>
        {mode === 'restricted' ? <LockOutlined /> : <GlobalOutlined />}
        <Select
          style={{ minWidth: 260 }}
          disabled={!a.canManage}
          loading={general.isPending || publicLink.set.isPending}
          value={mode}
          onChange={async (next: GeneralMode) => {
            // One choice for who may open the item: "anyone" is the item's link opening without sign-in; the others close it again.
            if (next === 'anyone') return publicLink.set.mutate({ on: true })
            if (publicLink.on) await publicLink.set.mutateAsync({ on: false })
            if (next !== a.generalAccess || publicLink.on)
              general.mutate({ mode: next, levelId: next === 'organization' ? (a.generalAccessLevelId ?? defaultLevel ?? null) : null })
          }}
          options={[
            { value: 'restricted', label: t('sharing.people.restricted') },
            { value: 'organization', label: t('sharing.people.organization') },
            ...(publicLink.enabled
              ? [{ value: 'anyone', label: t('sharing.people.anyone'), disabled: (publicLink.blocked && !publicLink.on) || !publicLink.canChange }]
              : []),
          ]}
          aria-label={t('sharing.people.generalAccess')}
        />
        {mode === 'anyone' && (
          <Select
            style={{ minWidth: 200 }}
            disabled={!publicLink.canChange}
            loading={publicLink.set.isPending}
            value={publicLink.levelId ?? undefined}
            options={publicLink.levelOptions}
            onChange={(v: string) => publicLink.set.mutate({ on: true, levelId: v })}
            aria-label={t('sharing.people.anyoneLevel')}
          />
        )}
        {mode === 'organization' && (
          <Select
            style={{ minWidth: 200 }}
            disabled={!a.canManage}
            value={a.generalAccessLevelId ?? undefined}
            options={optionsWith(a.generalAccessLevelId)}
            onChange={(v) => general.mutate({ mode: 'organization', levelId: v })}
            aria-label={t('sharing.people.level')}
          />
        )}
      </Space>
      <Typography.Paragraph type="secondary" style={{ fontSize: 12, marginTop: 4 }}>
        {mode === 'anyone'
          ? t('sharing.publicOnHint')
          : publicLink.blockedText ?? (mode === 'restricted' ? t('sharing.people.restrictedHint') : t('sharing.people.organizationHint'))}
      </Typography.Paragraph>

      {!isRoot && a.canManage && (
        <>
          <Divider orientation="left" orientationMargin={0}>
            {t('sharing.people.inheritance')}
          </Divider>
          {a.hasUniqueAcl ? (
            <Alert
              type="info"
              showIcon
              message={t('sharing.people.unique')}
              action={
                <Dropdown
                  menu={{
                    items: [
                      { key: 'one', label: t('sharing.people.restoreThis'), onClick: () => restore.mutate(false) },
                      { key: 'all', label: t('sharing.people.restoreAll'), onClick: () => restore.mutate(true) },
                    ],
                  }}
                >
                  <Button size="small">{t('sharing.people.restore')}</Button>
                </Dropdown>
              }
            />
          ) : (
            <Alert
              type="info"
              showIcon
              message={t('sharing.people.inherits', { name: a.inheritsFrom ?? '' })}
              action={
                <Dropdown
                  menu={{
                    items: [
                      { key: 'copy', label: t('sharing.people.stopCopy'), onClick: () => stop.mutate(true) },
                      { key: 'empty', label: t('sharing.people.stopEmpty'), onClick: () => stop.mutate(false) },
                    ],
                  }}
                >
                  <Button size="small">{t('sharing.people.stop')}</Button>
                </Dropdown>
              }
            />
          )}
        </>
      )}
    </>
  )
}
