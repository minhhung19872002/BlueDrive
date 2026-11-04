import { useState } from 'react'
import { Button, Card, Form, Input, InputNumber, List, Modal, Radio, Select, Space, Switch, Tag } from 'antd'
import { PlusOutlined, SettingOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { createLibrary, listLibraries, saveLibrary, type Library } from '@/api/libraries'
import LibraryColumnsPanel from './LibraryColumnsPanel'
import { useApiMutation } from '@/hooks/useApiMutation'

type SettingsForm = Omit<Library, 'id' | 'workspaceId' | 'isDefault' | 'folderId' | 'canManage'>

/** Spec 1.4 / 11.7 "Cài đặt Library": versions, approval, check-out, file types; and new libraries of the Space. */
export default function LibrarySettingsCard({ workspaceId, canCreate }: { workspaceId: string; canCreate: boolean }) {
  const { t } = useTranslation()
  const [editing, setEditing] = useState<Library | null>(null)
  const [creating, setCreating] = useState(false)
  const [name, setName] = useState('')
  const [form] = Form.useForm<SettingsForm>()
  const key = ['libraries', workspaceId]
  const libraries = useQuery({ queryKey: key, queryFn: () => listLibraries(workspaceId) })
  const save = useApiMutation((v: SettingsForm) => saveLibrary(workspaceId, editing!.id, v), {
    invalidate: [key],
    successMessage: t('library.saved'),
    onSuccess: () => setEditing(null),
  })
  const create = useApiMutation(() => createLibrary(workspaceId, name.trim()), {
    invalidate: [key, ['drive']],
    successMessage: t('library.created'),
    onSuccess: () => {
      setCreating(false)
      setName('')
    },
  })
  const open = (l: Library) => {
    setEditing(l)
    form.setFieldsValue(l)
  }
  return (
    <Card
      size="small"
      title={t('library.title')}
      extra={
        canCreate && (
          <Button size="small" icon={<PlusOutlined />} onClick={() => setCreating(true)}>
            {t('library.new')}
          </Button>
        )
      }
    >
      <List
        size="small"
        loading={libraries.isLoading}
        dataSource={libraries.data ?? []}
        renderItem={(l) => (
          <List.Item
            actions={
              l.canManage
                ? [
                    <Button key="edit" size="small" icon={<SettingOutlined />} onClick={() => open(l)}>
                      {t('library.settings')}
                    </Button>,
                  ]
                : []
            }
          >
            <Space wrap>
              <strong>{l.name}</strong>
              {l.isDefault && <Tag>{t('library.default')}</Tag>}
              <Tag color="blue">{t(`library.versioning.${l.versioning}`)}</Tag>
              {l.requireCheckout && <Tag>{t('library.requireCheckout')}</Tag>}
              {l.contentApproval && <Tag color="gold">{t('library.contentApproval')}</Tag>}
              {l.allowedExtensions.length > 0 && <Tag>{l.allowedExtensions.join(', ')}</Tag>}
            </Space>
          </List.Item>
        )}
      />
      {editing && (
        <Modal
          open
          width={560}
          title={t('library.settingsTitle', { name: editing.name })}
          okText={t('common.save')}
          okButtonProps={{ loading: save.isPending }}
          onOk={() => form.submit()}
          onCancel={() => setEditing(null)}
          destroyOnHidden
        >
          <Form<SettingsForm> form={form} layout="vertical" onFinish={(v) => save.mutate(v)}>
            <Form.Item name="name" label={t('library.name')} rules={[{ required: true, whitespace: true }]}>
              <Input maxLength={255} />
            </Form.Item>
            <Form.Item name="versioning" label={t('library.versioningLabel')} extra={t('library.versioningHint')}>
              <Radio.Group
                options={[
                  { value: 'major', label: t('library.versioning.major') },
                  { value: 'major_minor', label: t('library.versioning.major_minor') },
                ]}
              />
            </Form.Item>
            <Space wrap>
              <Form.Item name="majorLimit" label={t('library.majorLimit')}>
                <InputNumber min={1} max={50000} precision={0} />
              </Form.Item>
              <Form.Item name="minorLimit" label={t('library.minorLimit')}>
                <InputNumber min={0} max={1000} precision={0} />
              </Form.Item>
              <Form.Item name="deleteVersionsOlderThanDays" label={t('library.olderThan')}>
                <InputNumber min={1} max={36500} precision={0} placeholder={t('library.keepAll')} />
              </Form.Item>
            </Space>
            <Form.Item name="requireCheckout" label={t('library.requireCheckout')} valuePropName="checked" extra={t('library.requireCheckoutHint')}>
              <Switch />
            </Form.Item>
            <Form.Item name="contentApproval" label={t('library.contentApproval')} valuePropName="checked" extra={t('library.contentApprovalHint')}>
              <Switch />
            </Form.Item>
            <Form.Item name="allowedExtensions" label={t('library.allowedExtensions')} extra={t('library.allowedExtensionsHint')}>
              <Select mode="tags" tokenSeparators={[',', ' ', ';']} placeholder={t('library.asSystem')} />
            </Form.Item>
            <Form.Item name="maxFileSizeMb" label={t('library.maxFileSize')}>
              <InputNumber min={1} max={1000000} precision={0} addonAfter="MB" placeholder={t('library.asSystem')} />
            </Form.Item>
          </Form>
          <LibraryColumnsPanel workspaceId={workspaceId} libraryId={editing.id} />
        </Modal>
      )}
      {creating && (
        <Modal
          open
          title={t('library.new')}
          okText={t('library.create')}
          okButtonProps={{ disabled: !name.trim(), loading: create.isPending }}
          onOk={() => create.mutate(undefined)}
          onCancel={() => setCreating(false)}
          destroyOnHidden
        >
          <Input value={name} onChange={(e) => setName(e.target.value)} maxLength={255} placeholder={t('library.namePlaceholder')} aria-label={t('library.name')} />
        </Modal>
      )}
    </Card>
  )
}
