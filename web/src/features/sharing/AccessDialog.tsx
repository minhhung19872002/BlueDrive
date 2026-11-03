import { useMemo, useState } from 'react'
import {
  Alert,
  Button,
  DatePicker,
  Divider,
  Modal,
  Result,
  Select,
  Skeleton,
  Space,
  Switch,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd'
import { PlusOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import dayjs from 'dayjs'
import { useTranslation } from 'react-i18next'
import { updateFolder } from '@/api/drive'
import { getErrorMessage } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import { getResourceAccess, setResourceAccess } from '@/api/sharing'
import { useFolderInfo } from '@/features/drive/hooks'
import { useApiMutation } from '@/hooks/useApiMutation'
import { accessRank, type AccessEntry, type AccessLevel, type PrincipalType, type ResourceAccessList, type ResourceType } from '@/types/sharing'
import { formatDateTime, parseUtc } from '@/utils/format'
import { ACL_LEVELS, LEVEL_COLORS, SOURCE_COLORS, splitPrincipal, type PrincipalValue } from './levels'
import PrincipalIcon from './PrincipalIcon'
import PrincipalSelect from './PrincipalSelect'

export interface AccessTarget {
  type: ResourceType
  id: string
  name: string
  workspaceId: string
}

/** One principal = one row (like SharePoint "Quản lý quyền truy cập"), whatever the number of grants behind it. */
interface Row {
  key: string
  principalType: PrincipalType
  principalId: string
  principalName: string
  /** Direct permission on this item. */
  direct: { level: AccessLevel; inherit: boolean; expiresAt: string | null } | null
  /** Share made on this item ("Chia sẻ"); its level is at most "Chỉnh sửa". */
  share: { id: string; level: AccessLevel } | null
  /** Access coming from the parent folders / the workspace (gone once the item stops inheriting). */
  inherited: { level: AccessLevel; sources: AccessEntry[] } | null
  /** Owner / workspace managers: always have access, cannot be changed here. */
  fixed: AccessEntry | null
}

const REMOVE = 'remove'
const max = (...levels: (AccessLevel | null | undefined)[]): AccessLevel =>
  levels.reduce<AccessLevel>((m, l) => (l && accessRank(l) > accessRank(m) ? l : m), 'none')

function buildRows(entries: AccessEntry[]): Record<string, Row> {
  const rows: Record<string, Row> = {}
  for (const e of entries) {
    const key = `${e.principalType}:${e.principalId}`
    const r = (rows[key] ??= {
      key,
      principalType: e.principalType,
      principalId: e.principalId,
      principalName: e.principalName,
      direct: null,
      share: null,
      inherited: null,
      fixed: null,
    })
    if (e.source === 'owner' || (e.source === 'member' && e.accessLevel === 'manage')) r.fixed = e
    else if (e.source === 'direct') r.direct = { level: e.accessLevel, inherit: e.inherit, expiresAt: e.expiresAt }
    else if (e.source === 'share' && !e.fromParent && e.id) r.share = { id: e.id, level: e.accessLevel }
    else r.inherited = { level: max(r.inherited?.level, e.accessLevel), sources: [...(r.inherited?.sources ?? []), e] }
  }
  return rows
}

/** "Cần phê duyệt khi tải lên" switch of a folder (managers). */
function ApprovalSetting({ workspaceId, folderId }: { workspaceId: string; folderId: string }) {
  const { t } = useTranslation()
  const info = useFolderInfo(workspaceId, folderId)
  const update = useApiMutation((requiresApproval: boolean) => updateFolder(folderId, { requiresApproval }), {
    successMessage: (_, v) => (v ? t('access.approvalOn') : t('access.approvalOff')),
    invalidate: [qk.folderInfo(workspaceId, folderId)],
  })
  if (!info.data?.access.canManage) return null
  return (
    <>
      <Divider style={{ margin: '16px 0' }} />
      <Space align="start">
        <Switch checked={info.data.requiresApproval} loading={update.isPending} onChange={(v) => update.mutate(v)} />
        <div>
          <Typography.Text strong>{t('access.requiresApproval')}</Typography.Text>
          <br />
          <Typography.Text type="secondary">{t('access.requiresApprovalHint')}</Typography.Text>
        </div>
      </Space>
    </>
  )
}

function AccessEditor({ target, data, onClose }: { target: AccessTarget; data: ResourceAccessList; onClose: () => void }) {
  const { t } = useTranslation()
  const [modal, modalHolder] = Modal.useModal()
  const canInherit = target.type !== 'workspace'
  const canManage = data.canManage
  const [unique, setUnique] = useState(data.breakInheritance)
  const [rows, setRows] = useState<Record<string, Row>>(() => buildRows(data.entries))
  const [newPrincipal, setNewPrincipal] = useState<PrincipalValue | null>(null)
  const [newLevel, setNewLevel] = useState<AccessLevel>('view')
  const hadShares = data.entries.some((e) => e.source === 'share' && !e.fromParent)
  const parentName = useMemo(() => {
    const e = data.entries.find((x) => x.fromParent && x.source !== 'member')
    return e?.sourceName?.match(/"(.+)"/)?.[1] ?? null
  }, [data.entries])

  const save = useApiMutation(
    () =>
      setResourceAccess(target.type, target.id, {
        entries: Object.values(rows)
          .filter((r) => r.direct)
          .map((r) => ({
            principalType: r.principalType,
            principalId: r.principalId,
            accessLevel: r.direct!.level,
            inherit: r.direct!.inherit,
            expiresAt: r.direct!.expiresAt,
          })),
        breakInheritance: canInherit ? unique : null,
        copyInheritedOnBreak: false, // copied here, row by row
        shares: hadShares ? Object.values(rows).filter((r) => r.share).map((r) => ({ id: r.share!.id, accessLevel: r.share!.level })) : null,
      }),
    { successMessage: t('access.saved'), invalidate: [qk.drive], onSuccess: onClose },
  )

  /** SharePoint "Ngừng kế thừa quyền": what came from the parent becomes direct permissions of the item. */
  const copyInherited = (list: Record<string, Row>): Record<string, Row> =>
    Object.fromEntries(
      Object.entries(list).map(([k, r]) => [
        k,
        r.inherited
          ? { ...r, direct: { level: max(r.direct?.level, r.inherited.level), inherit: r.direct?.inherit ?? true, expiresAt: r.direct?.expiresAt ?? null }, inherited: null }
          : r,
      ]),
    )

  const confirmBreak = (name: string | null, then: () => void) =>
    modal.confirm({
      title: t('access.stopInheritTitle'),
      content: name ? t('access.stopInheritForChange', { name }) : t('access.stopInheritText'),
      okText: t('access.stopInherit'),
      cancelText: t('common.cancel'),
      onOk: () => {
        setUnique(true)
        setRows((list) => copyInherited(list))
        then()
      },
    })

  const restoreInheritance = () =>
    modal.confirm({
      title: t('access.restoreInheritTitle'),
      content: t('access.restoreInheritText'),
      okText: t('access.restoreInherit'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: () => {
        setUnique(false)
        setRows((list) =>
          Object.fromEntries(Object.entries(list).filter(([, r]) => r.fixed).map(([k, r]) => [k, { ...r, direct: null, share: null }])),
        )
      },
    })

  /** New level (or removal) of one row, on the item's own grants. */
  const apply = (key: string, level: AccessLevel | typeof REMOVE) =>
    setRows((list) => {
      const r = list[key]
      if (!r) return list
      if (level === REMOVE) {
        const next = { ...list }
        if (r.inherited || r.fixed) next[key] = { ...r, direct: null, share: null }
        else delete next[key]
        return next
      }
      const shareLevel: AccessLevel = accessRank(level) > accessRank('edit') ? 'edit' : level
      const needsDirect = !r.share || accessRank(level) > accessRank('edit') || !!r.direct
      return {
        ...list,
        [key]: {
          ...r,
          share: r.share ? { ...r.share, level: shareLevel } : null,
          direct: needsDirect ? { level, inherit: r.direct?.inherit ?? true, expiresAt: r.direct?.expiresAt ?? null } : null,
        },
      }
    })

  const change = (r: Row, level: AccessLevel | typeof REMOVE) => {
    // Lowering or removing what comes from the parent is only possible once the item stops inheriting (SharePoint).
    const lowersInherited = !unique && !!r.inherited && (level === REMOVE || accessRank(level) < accessRank(r.inherited.level))
    if (lowersInherited) confirmBreak(r.principalName, () => apply(r.key, level))
    else apply(r.key, level)
  }

  const addRow = () => {
    if (!newPrincipal) return
    const { type, id } = splitPrincipal(newPrincipal.value)
    const key = `${type}:${id}`
    setRows((list) => ({
      ...list,
      [key]: {
        ...(list[key] ?? { key, principalType: type, principalId: id, principalName: newPrincipal.label, share: null, inherited: null, fixed: null }),
        direct: { level: newLevel, inherit: true, expiresAt: null },
      },
    }))
    setNewPrincipal(null)
  }

  const visible = Object.values(rows)
    .map((r) => ({ r, level: max(r.fixed?.accessLevel, r.direct?.level, r.share?.level, unique ? null : r.inherited?.level) }))
    .filter((x) => x.level !== 'none')
    .sort((a, b) => Number(!!b.r.fixed) - Number(!!a.r.fixed) || accessRank(b.level) - accessRank(a.level) || a.r.principalName.localeCompare(b.r.principalName, 'vi'))

  const sourcesOf = (r: Row) => {
    const tags: { key: string; color: string; label: string; hint?: string | null }[] = []
    if (r.fixed) tags.push({ key: 'fixed', color: SOURCE_COLORS[r.fixed.source], label: t(`access.sources.${r.fixed.source}`), hint: r.fixed.sourceName })
    if (r.direct) tags.push({ key: 'direct', color: SOURCE_COLORS.direct, label: t('access.sources.direct') })
    if (r.share) tags.push({ key: 'share', color: SOURCE_COLORS.share, label: t('access.sources.share') })
    if (!unique)
      for (const s of r.inherited?.sources ?? [])
        tags.push({ key: `${s.source}:${s.id ?? s.sourceName}`, color: SOURCE_COLORS[s.source], label: t(`access.sources.${s.source}`), hint: s.sourceName })
    return tags
  }

  return (
    <Modal
      open
      title={t('access.title', { name: data.name })}
      onCancel={onClose}
      width={880}
      okText={t('common.save')}
      cancelText={canManage ? t('common.cancel') : t('common.close')}
      onOk={() => save.mutate()}
      okButtonProps={{ style: canManage ? undefined : { display: 'none' } }}
      confirmLoading={save.isPending}
      destroyOnHidden
    >
      {modalHolder}
      {!canManage && <Alert type="info" showIcon message={t('access.readOnly')} style={{ marginBottom: 12 }} />}

      {canInherit && (
        <Alert
          type={unique ? 'warning' : 'info'}
          showIcon
          style={{ marginBottom: 12 }}
          message={unique ? t('access.uniqueState') : parentName ? t('access.inheritsFrom', { name: parentName }) : t('access.inheritsState')}
          action={
            canManage &&
            (unique ? (
              <Button size="small" danger onClick={restoreInheritance}>
                {t('access.restoreInherit')}
              </Button>
            ) : (
              <Button size="small" onClick={() => confirmBreak(null, () => undefined)}>
                {t('access.stopInherit')}
              </Button>
            ))
          }
        />
      )}

      {canManage && (
        <Space.Compact style={{ width: '100%', marginBottom: 12 }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <PrincipalSelect types={['user', 'group', 'organization', 'role']} value={newPrincipal} onChange={setNewPrincipal} />
          </div>
          <Select
            value={newLevel}
            onChange={setNewLevel}
            style={{ width: 170 }}
            aria-label={t('access.level')}
            options={ACL_LEVELS.map((l) => ({ value: l, label: t(`sharing.levels.${l}`) }))}
          />
          <Button icon={<PlusOutlined />} onClick={addRow} disabled={!newPrincipal}>
            {t('access.add')}
          </Button>
        </Space.Compact>
      )}

      <Typography.Title level={5}>{t('access.peopleTitle')}</Typography.Title>
      <Table<{ r: Row; level: AccessLevel }>
        size="small"
        rowKey={(x) => x.r.key}
        dataSource={visible}
        pagination={visible.length > 12 ? { pageSize: 12, size: 'small' } : false}
        scroll={{ x: 680 }}
        locale={{ emptyText: t('access.nobody') }}
        columns={[
          {
            title: t('access.principal'),
            key: 'principal',
            render: (_, { r }) => (
              <Space>
                <PrincipalIcon type={r.principalType} />
                {r.principalName}
              </Space>
            ),
          },
          {
            title: t('access.level'),
            key: 'level',
            width: 190,
            render: (_, { r, level }) =>
              canManage && !r.fixed ? (
                <Select
                  size="small"
                  value={level}
                  style={{ width: '100%' }}
                  aria-label={t('access.levelOf', { name: r.principalName })}
                  onChange={(v: AccessLevel | typeof REMOVE) => change(r, v)}
                  options={[
                    ...ACL_LEVELS.map((l) => ({ value: l, label: t(`sharing.levels.${l}`) })),
                    { value: REMOVE, label: <Typography.Text type="danger">{t('access.removeAccess')}</Typography.Text> },
                  ]}
                />
              ) : (
                <Tag color={LEVEL_COLORS[level]}>{t(`sharing.levels.${level}`)}</Tag>
              ),
          },
          {
            title: t('access.source'),
            key: 'source',
            render: (_, { r }) => (
              <Space size={[4, 4]} wrap>
                {sourcesOf(r).map((s) => (
                  <Tooltip key={s.key} title={s.hint}>
                    <Tag color={s.color} style={{ marginInlineEnd: 0 }}>
                      {s.hint && s.key !== 'fixed' ? s.hint : s.label}
                    </Tag>
                  </Tooltip>
                ))}
              </Space>
            ),
          },
          {
            title: t('access.expiresAt'),
            key: 'expiresAt',
            width: 150,
            render: (_, { r }) =>
              canManage && r.direct && !r.fixed ? (
                <DatePicker
                  size="small"
                  format="DD/MM/YYYY"
                  placeholder={t('access.noExpiry')}
                  value={r.direct.expiresAt ? parseUtc(r.direct.expiresAt) : null}
                  disabledDate={(d) => d.isBefore(dayjs(), 'day')}
                  onChange={(d) =>
                    setRows((list) => ({ ...list, [r.key]: { ...r, direct: { ...r.direct!, expiresAt: d ? d.endOf('day').toISOString() : null } } }))
                  }
                />
              ) : r.direct?.expiresAt ? (
                formatDateTime(r.direct.expiresAt)
              ) : (
                <Typography.Text type="secondary">{t('access.noExpiry')}</Typography.Text>
              ),
          },
        ]}
      />
      {unique && canManage && <Typography.Paragraph type="secondary" style={{ marginTop: 8 }}>{t('access.uniqueHint')}</Typography.Paragraph>}

      {target.type === 'folder' && <ApprovalSetting workspaceId={target.workspaceId} folderId={target.id} />}
    </Modal>
  )
}

/** #56 "Quản lý quyền truy cập" for a folder / file / workspace (SharePoint-style). */
export default function AccessDialog({ target, onClose }: { target: AccessTarget; onClose: () => void }) {
  const { t } = useTranslation()
  const access = useQuery({
    queryKey: qk.access(target.type, target.id),
    queryFn: () => getResourceAccess(target.type, target.id),
    staleTime: 0,
    retry: 0,
  })
  if (access.isLoading || access.isError || !access.data) {
    return (
      <Modal open title={t('access.title', { name: target.name })} onCancel={onClose} footer={null} width={880}>
        {access.isError ? <Result status="warning" subTitle={getErrorMessage(access.error)} /> : <Skeleton active />}
      </Modal>
    )
  }
  return <AccessEditor target={target} data={access.data} onClose={onClose} />
}
