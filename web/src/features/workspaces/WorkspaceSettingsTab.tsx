import { useEffect, useState } from 'react'
import { Alert, Button, Card, Form, InputNumber, List, Popconfirm, Select, Space, Switch, Tag, Typography } from 'antd'
import { DeleteOutlined, InboxOutlined, SaveOutlined, UndoOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { qk } from '@/api/queryKeys'
import {
  addSpaceGroupMembers,
  deleteWorkspace,
  getSpaceSettings,
  listSpaceGroupMembers,
  removeSpaceGroupMember,
  setSpaceSettings,
  setSpaceStatus,
  type SpaceSettings,
} from '@/api/workspaces'
import { splitPrincipal, type PrincipalValue } from '@/features/sharing/levels'
import PrincipalSelect from '@/features/sharing/PrincipalSelect'
import { useApiMutation } from '@/hooks/useApiMutation'
import type { WorkspaceMemberRole } from '@/types/drive'
import WorkspaceSharingTab from './WorkspaceSharingTab'

const GROUP_ROLES: WorkspaceMemberRole[] = ['manager', 'editor', 'uploader', 'approver', 'viewer']

type SettingsForm = Pick<SpaceSettings, 'allowDownload' | 'allowSync' | 'trashRetentionDays' | 'activityForMembers' | 'allowMoveOut'>

/** Spec 6: the Space's member groups (user groups, units), settings, status (archive / delete) and sharing policy. */
export default function WorkspaceSettingsTab({ workspaceId, name }: { workspaceId: string; name: string }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [form] = Form.useForm<SettingsForm>()
  const settings = useQuery({ queryKey: ['space-settings', workspaceId], queryFn: () => getSpaceSettings(workspaceId) })
  const groups = useQuery({ queryKey: ['space-group-members', workspaceId], queryFn: () => listSpaceGroupMembers(workspaceId) })
  const [picked, setPicked] = useState<PrincipalValue[]>([])
  const [role, setRole] = useState<WorkspaceMemberRole>('editor')
  const invalidate = [['space-settings', workspaceId], ['space-group-members', workspaceId], qk.workspace(workspaceId), qk.workspaces]

  const save = useApiMutation(
    (v: SettingsForm) => setSpaceSettings(workspaceId, { ...v, trashRetentionDays: v.trashRetentionDays ?? null, coverFileId: settings.data?.coverFileId ?? null }),
    { invalidate, successMessage: t('spaceSettings.saved') },
  )
  const add = useApiMutation(
    () =>
      addSpaceGroupMembers(
        workspaceId,
        picked.map((p) => {
          const { type, id } = splitPrincipal(p.value)
          return { kind: type === 'organization' ? 'org_unit_tree' : 'group', id }
        }),
        role,
      ),
    { invalidate, successMessage: t('spaceSettings.groupsAdded'), onSuccess: () => setPicked([]) },
  )
  const remove = useApiMutation((principalId: string) => removeSpaceGroupMember(workspaceId, principalId), { invalidate, successMessage: t('spaceSettings.groupRemoved') })
  const status = useApiMutation((s: 'active' | 'archived') => setSpaceStatus(workspaceId, s), { invalidate, successMessage: t('spaceSettings.statusSaved') })
  const del = useApiMutation(() => deleteWorkspace(workspaceId), {
    invalidate: [qk.workspaces],
    successMessage: t('spaceSettings.deleted'),
    onSuccess: () => navigate('/workspaces'),
  })

  useEffect(() => {
    if (settings.data) form.setFieldsValue(settings.data)
  }, [settings.data, form])

  if (!settings.data) return null
  const s = settings.data
  const can = s.canEdit
  return (
    <Space direction="vertical" size="middle" style={{ width: '100%', maxWidth: 760 }}>
      {s.status === 'archived' && <Alert type="warning" showIcon message={t('spaceSettings.archivedNotice')} />}

      <Card size="small" title={t('spaceSettings.groupsTitle')}>
        <Typography.Paragraph type="secondary" style={{ fontSize: 12 }}>
          {t('spaceSettings.groupsHint')}
        </Typography.Paragraph>
        <List
          size="small"
          dataSource={groups.data ?? []}
          locale={{ emptyText: t('spaceSettings.noGroups') }}
          renderItem={(g) => (
            <List.Item
              actions={
                can
                  ? [
                      <Popconfirm key="del" title={t('spaceSettings.removeGroupConfirm', { name: g.name })} onConfirm={() => remove.mutate(g.principalId)}>
                        <Button size="small" type="text" danger icon={<DeleteOutlined />} aria-label={t('common.delete')} />
                      </Popconfirm>,
                    ]
                  : []
              }
            >
              <Space>
                <span>{g.name}</span>
                <Tag>{t(`workspace.roles.${g.memberRole}`)}</Tag>
              </Space>
            </List.Item>
          )}
        />
        {can && (
          <Space wrap style={{ marginTop: 8, width: '100%' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <PrincipalSelect multiple types={['group', 'organization']} value={picked} onChange={setPicked} placeholder={t('spaceSettings.addGroups')} />
            </div>
            <Select style={{ minWidth: 180 }} value={role} onChange={setRole} options={GROUP_ROLES.map((r) => ({ value: r, label: t(`workspace.roles.${r}`) }))} />
            <Button type="primary" disabled={picked.length === 0} loading={add.isPending} onClick={() => add.mutate(undefined)}>
              {t('spaceSettings.add')}
            </Button>
          </Space>
        )}
      </Card>

      <Card size="small" title={t('spaceSettings.title')}>
        <Form<SettingsForm> form={form} layout="vertical" disabled={!can} onFinish={(v) => save.mutate(v)}>
          <Form.Item name="allowDownload" label={t('spaceSettings.allowDownload')} valuePropName="checked" extra={t('spaceSettings.allowDownloadHint')}>
            <Switch />
          </Form.Item>
          <Form.Item name="allowSync" label={t('spaceSettings.allowSync')} valuePropName="checked">
            <Switch />
          </Form.Item>
          <Form.Item name="activityForMembers" label={t('spaceSettings.activityForMembers')} valuePropName="checked">
            <Switch />
          </Form.Item>
          <Form.Item name="allowMoveOut" label={t('spaceSettings.allowMoveOut')} valuePropName="checked" extra={t('spaceSettings.allowMoveOutHint')}>
            <Switch />
          </Form.Item>
          <Form.Item name="trashRetentionDays" label={t('spaceSettings.trashDays')} extra={t('spaceSettings.trashDaysHint')}>
            <InputNumber min={1} max={3650} precision={0} style={{ width: 200 }} placeholder={t('spaceSettings.asSystem')} />
          </Form.Item>
          {can && (
            <Button type="primary" htmlType="submit" icon={<SaveOutlined />} loading={save.isPending}>
              {t('common.save')}
            </Button>
          )}
        </Form>
      </Card>

      <WorkspaceSharingTab workspaceId={workspaceId} />

      {can && (
        <Card size="small" title={t('spaceSettings.statusTitle')}>
          <Space wrap>
            {s.status === 'archived' ? (
              <Button icon={<UndoOutlined />} loading={status.isPending} onClick={() => status.mutate('active')}>
                {t('spaceSettings.unarchive')}
              </Button>
            ) : (
              <Popconfirm title={t('spaceSettings.archiveConfirm')} onConfirm={() => status.mutate('archived')}>
                <Button icon={<InboxOutlined />} loading={status.isPending}>
                  {t('spaceSettings.archive')}
                </Button>
              </Popconfirm>
            )}
            <Popconfirm title={t('spaceSettings.deleteConfirm', { name })} okButtonProps={{ danger: true }} onConfirm={() => del.mutate(undefined)}>
              <Button danger icon={<DeleteOutlined />} loading={del.isPending}>
                {t('spaceSettings.delete')}
              </Button>
            </Popconfirm>
          </Space>
        </Card>
      )}
    </Space>
  )
}
