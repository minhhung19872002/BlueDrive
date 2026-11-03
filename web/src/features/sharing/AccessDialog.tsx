import { useState } from 'react'
import {
  Alert,
  Button,
  Checkbox,
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
import { DeleteOutlined, PlusOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import dayjs from 'dayjs'
import { useTranslation } from 'react-i18next'
import { updateFolder } from '@/api/drive'
import { getErrorMessage } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import { getResourceAccess, setResourceAccess } from '@/api/sharing'
import { useFolderInfo } from '@/features/drive/hooks'
import { useApiMutation } from '@/hooks/useApiMutation'
import type { AccessEntry, AccessLevel, PrincipalType, ResourceAccessList, ResourceType } from '@/types/sharing'
import { formatDateTime, parseUtc } from '@/utils/format'
import { ACL_LEVELS, LEVEL_COLORS, SHARE_LEVELS, SOURCE_COLORS, splitPrincipal, type PrincipalValue } from './levels'
import PrincipalIcon from './PrincipalIcon'
import PrincipalSelect from './PrincipalSelect'

export interface AccessTarget {
  type: ResourceType
  id: string
  name: string
  workspaceId: string
}

interface DirectRow {
  key: string
  principalType: PrincipalType
  principalId: string
  principalName: string
  accessLevel: AccessLevel
  inherit: boolean
  expiresAt: string | null
}

function toRows(entries: AccessEntry[]): DirectRow[] {
  return entries
    .filter((e) => e.source === 'direct')
    .map((e) => ({
      key: `${e.principalType}:${e.principalId}`,
      principalType: e.principalType,
      principalId: e.principalId,
      principalName: e.principalName,
      accessLevel: e.accessLevel,
      inherit: e.inherit,
      expiresAt: e.expiresAt,
    }))
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
  const isFolder = target.type === 'folder'
  const [rows, setRows] = useState<DirectRow[]>(() => toRows(data.entries))
  const [breakInheritance, setBreakInheritance] = useState(data.breakInheritance)
  const [copyInherited, setCopyInherited] = useState(false)
  const [breakPrompt, setBreakPrompt] = useState(false)
  const [newPrincipal, setNewPrincipal] = useState<PrincipalValue | null>(null)
  const [newLevel, setNewLevel] = useState<AccessLevel>('view')
  const canManage = data.canManage
  // Shares of this very item (made with "Chia sẻ"): their level can be changed or the share removed here.
  const ownShares = data.entries.filter((e) => e.source === 'share' && e.editable && e.id)
  const [shareLevels, setShareLevels] = useState<Record<string, AccessLevel>>(() =>
    Object.fromEntries(ownShares.map((e) => [e.id!, e.accessLevel])),
  )

  const save = useApiMutation(
    () =>
      setResourceAccess(target.type, target.id, {
        entries: rows.map((r) => ({
          principalType: r.principalType,
          principalId: r.principalId,
          accessLevel: r.accessLevel,
          inherit: r.inherit,
          expiresAt: r.expiresAt,
        })),
        breakInheritance: isFolder ? breakInheritance : null,
        copyInheritedOnBreak: copyInherited,
        shares: ownShares.length > 0 ? Object.entries(shareLevels).map(([id, accessLevel]) => ({ id, accessLevel })) : null,
      }),
    {
      successMessage: t('access.saved'),
      invalidate: [qk.drive],
      onSuccess: onClose,
    },
  )

  const addRow = () => {
    if (!newPrincipal) return
    const { type, id } = splitPrincipal(newPrincipal.value)
    setRows((list) => [
      ...list.filter((r) => !(r.principalType === type && r.principalId === id)),
      { key: newPrincipal.value, principalType: type, principalId: id, principalName: newPrincipal.label, accessLevel: newLevel, inherit: true, expiresAt: null },
    ])
    setNewPrincipal(null)
  }

  const patchRow = (key: string, changes: Partial<DirectRow>) =>
    setRows((list) => list.map((r) => (r.key === key ? { ...r, ...changes } : r)))

  const readOnly = data.entries.filter((e) => e.source !== 'direct' && !(e.editable && e.id && e.source === 'share' && !(e.id in shareLevels)))
  const isOwnShare = (e: AccessEntry) => canManage && e.source === 'share' && e.editable && !!e.id
  const inheritedHidden = breakInheritance && !data.breakInheritance

  return (
    <Modal
      open
      title={t('access.title', { name: data.name })}
      onCancel={onClose}
      width={860}
      okText={t('common.save')}
      cancelText={canManage ? t('common.cancel') : t('common.close')}
      onOk={() => save.mutate()}
      okButtonProps={{ style: canManage ? undefined : { display: 'none' } }}
      confirmLoading={save.isPending}
      destroyOnHidden
    >
      {!canManage && <Alert type="info" showIcon message={t('access.readOnly')} style={{ marginBottom: 12 }} />}

      {isFolder && canManage && (
        <Space align="start" style={{ marginBottom: 12 }}>
          <Switch
            checked={breakInheritance}
            onChange={(on) => {
              if (on && !data.breakInheritance) setBreakPrompt(true)
              else {
                setBreakInheritance(on)
                setCopyInherited(false)
              }
            }}
          />
          <div>
            <Typography.Text strong>{t('access.breakInheritance')}</Typography.Text>
            <br />
            <Typography.Text type="secondary">
              {breakInheritance
                ? copyInherited
                  ? t('access.breakCopyHint')
                  : t('access.breakOnHint')
                : t('access.breakOffHint')}
            </Typography.Text>
          </div>
        </Space>
      )}

      <Typography.Title level={5}>{t('access.directTitle')}</Typography.Title>
      <Table<DirectRow>
        size="small"
        rowKey="key"
        dataSource={rows}
        pagination={false}
        scroll={{ x: 640 }}
        locale={{ emptyText: t('access.noDirect') }}
        columns={[
          {
            title: t('access.principal'),
            key: 'principal',
            render: (_, r) => (
              <Space>
                <PrincipalIcon type={r.principalType} />
                {r.principalName}
              </Space>
            ),
          },
          {
            title: t('access.level'),
            key: 'level',
            width: 170,
            render: (_, r) =>
              canManage ? (
                <Select
                  size="small"
                  value={r.accessLevel}
                  style={{ width: '100%' }}
                  onChange={(v: AccessLevel) => patchRow(r.key, { accessLevel: v })}
                  options={ACL_LEVELS.map((l) => ({ value: l, label: t(`sharing.levels.${l}`) }))}
                />
              ) : (
                <Tag color={LEVEL_COLORS[r.accessLevel]}>{t(`sharing.levels.${r.accessLevel}`)}</Tag>
              ),
          },
          ...(isFolder
            ? [
                {
                  title: <Tooltip title={t('access.inheritHint')}>{t('access.inherit')}</Tooltip>,
                  key: 'inherit',
                  width: 90,
                  render: (_: unknown, r: DirectRow) => (
                    <Checkbox checked={r.inherit} disabled={!canManage} onChange={(e) => patchRow(r.key, { inherit: e.target.checked })} />
                  ),
                },
              ]
            : []),
          {
            title: t('access.expiresAt'),
            key: 'expiresAt',
            width: 160,
            render: (_, r) =>
              canManage ? (
                <DatePicker
                  size="small"
                  format="DD/MM/YYYY"
                  placeholder={t('access.noExpiry')}
                  value={r.expiresAt ? parseUtc(r.expiresAt) : null}
                  disabledDate={(d) => d.isBefore(dayjs(), 'day')}
                  onChange={(d) => patchRow(r.key, { expiresAt: d ? d.endOf('day').toISOString() : null })}
                />
              ) : (
                formatDateTime(r.expiresAt) || t('access.noExpiry')
              ),
          },
          ...(canManage
            ? [
                {
                  key: 'remove',
                  width: 48,
                  render: (_: unknown, r: DirectRow) => (
                    <Button
                      type="text"
                      danger
                      size="small"
                      icon={<DeleteOutlined />}
                      aria-label={t('common.delete')}
                      onClick={() => setRows((list) => list.filter((x) => x.key !== r.key))}
                    />
                  ),
                },
              ]
            : []),
        ]}
      />
      {canManage && (
        <Space.Compact style={{ width: '100%', marginTop: 8 }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <PrincipalSelect types={['user', 'group', 'organization', 'role']} value={newPrincipal} onChange={setNewPrincipal} />
          </div>
          <Select
            value={newLevel}
            onChange={setNewLevel}
            style={{ width: 160 }}
            options={ACL_LEVELS.map((l) => ({ value: l, label: t(`sharing.levels.${l}`) }))}
          />
          <Button icon={<PlusOutlined />} onClick={addRow} disabled={!newPrincipal}>
            {t('access.add')}
          </Button>
        </Space.Compact>
      )}

      <Typography.Title level={5} style={{ marginTop: 20 }}>
        {t('access.otherTitle')}
      </Typography.Title>
      {inheritedHidden && <Alert type="warning" showIcon message={t('access.breakPending')} style={{ marginBottom: 8 }} />}
      <Table<AccessEntry>
        size="small"
        rowKey={(e) => `${e.source}:${e.principalType}:${e.principalId}:${e.id ?? ''}`}
        dataSource={readOnly}
        pagination={readOnly.length > 10 ? { pageSize: 10, size: 'small' } : false}
        scroll={{ x: 560 }}
        columns={[
          {
            title: t('access.principal'),
            key: 'principal',
            render: (_, e) => (
              <Space>
                <PrincipalIcon type={e.principalType} />
                {e.principalName}
              </Space>
            ),
          },
          {
            title: t('access.level'),
            key: 'level',
            width: 150,
            render: (_, e) =>
              isOwnShare(e) ? (
                <Select
                  size="small"
                  value={shareLevels[e.id!]}
                  style={{ width: '100%' }}
                  aria-label={t('access.level')}
                  onChange={(v: AccessLevel) => setShareLevels((m) => ({ ...m, [e.id!]: v }))}
                  options={SHARE_LEVELS.map((l) => ({ value: l, label: t(`sharing.levels.${l}`) }))}
                />
              ) : (
                <Tag color={LEVEL_COLORS[e.accessLevel]}>{t(`sharing.levels.${e.accessLevel}`)}</Tag>
              ),
          },
          {
            title: t('access.source'),
            key: 'source',
            render: (_, e) => (
              <Tooltip title={e.sourceName}>
                <Tag color={SOURCE_COLORS[e.source]}>{t(`access.sources.${e.source}`)}</Tag>
                {e.sourceName && e.source !== 'owner' && (
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {e.sourceName}
                  </Typography.Text>
                )}
              </Tooltip>
            ),
          },
          {
            title: t('access.expiresAt'),
            key: 'expiresAt',
            width: 140,
            render: (_, e) => (e.expiresAt ? formatDateTime(e.expiresAt) : <Typography.Text type="secondary">{t('access.noExpiry')}</Typography.Text>),
          },
          ...(canManage
            ? [
                {
                  key: 'remove',
                  width: 48,
                  render: (_: unknown, e: AccessEntry) =>
                    isOwnShare(e) ? (
                      <Tooltip title={t('access.removeShare')}>
                        <Button
                          type="text"
                          danger
                          size="small"
                          icon={<DeleteOutlined />}
                          aria-label={t('access.removeShare')}
                          onClick={() =>
                            setShareLevels((m) => {
                              const next = { ...m }
                              delete next[e.id!]
                              return next
                            })
                          }
                        />
                      </Tooltip>
                    ) : null,
                },
              ]
            : []),
        ]}
      />

      {isFolder && <ApprovalSetting workspaceId={target.workspaceId} folderId={target.id} />}

      <Modal
        open={breakPrompt}
        title={t('access.breakConfirmTitle')}
        onCancel={() => setBreakPrompt(false)}
        footer={[
          <Button key="cancel" onClick={() => setBreakPrompt(false)}>
            {t('common.cancel')}
          </Button>,
          <Button
            key="nocopy"
            onClick={() => {
              setBreakInheritance(true)
              setCopyInherited(false)
              setBreakPrompt(false)
            }}
          >
            {t('access.breakNoCopy')}
          </Button>,
          <Button
            key="copy"
            type="primary"
            onClick={() => {
              setBreakInheritance(true)
              setCopyInherited(true)
              setBreakPrompt(false)
            }}
          >
            {t('access.breakCopy')}
          </Button>,
        ]}
      >
        <Typography.Paragraph>{t('access.breakConfirmText')}</Typography.Paragraph>
      </Modal>
    </Modal>
  )
}

/** #56 "Quản lý quyền truy cập" for a folder / file / workspace. */
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
      <Modal open title={t('access.title', { name: target.name })} onCancel={onClose} footer={null} width={860}>
        {access.isError ? <Result status="warning" subTitle={getErrorMessage(access.error)} /> : <Skeleton active />}
      </Modal>
    )
  }
  return <AccessEditor target={target} data={access.data} onClose={onClose} />
}
