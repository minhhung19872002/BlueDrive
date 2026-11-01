import { useState } from 'react'
import { App, Button, Card, Col, Empty, Input, List, Row, Skeleton, Space, Tag, Tooltip, Typography } from 'antd'
import { DeleteOutlined, EditOutlined, LockOutlined, PlusOutlined, TableOutlined, TeamOutlined } from '@ant-design/icons'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { showApiError } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import { deleteRole, listRoles } from '@/api/roles'
import PageHeader from '@/components/PageHeader'
import { useCan } from '@/hooks/usePermissions'
import type { RoleDto } from '@/types/admin'
import { normalizeText } from '@/utils/tree'
import PermissionMatrixEditor from './PermissionMatrixEditor'
import RoleFormModal from './RoleFormModal'

/** HSMT #7–8: roles (system roles are protected) and the permission matrix of the selected role. */
export default function RolesPage() {
  const { t } = useTranslation()
  const can = useCan()
  const navigate = useNavigate()
  const { modal, message } = App.useApp()
  const queryClient = useQueryClient()
  const [keyword, setKeyword] = useState('')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [form, setForm] = useState<{ open: boolean; editing: RoleDto | null }>({ open: false, editing: null })

  const roles = useQuery({ queryKey: [...qk.roles, 'list', ''], queryFn: () => listRoles() })
  const term = normalizeText(keyword.trim())
  const visible = (roles.data ?? []).filter((r) => !term || normalizeText(`${r.name} ${r.code}`).includes(term))
  const selected = roles.data?.find((r) => r.id === selectedId) ?? roles.data?.[0]

  const confirmDelete = (role: RoleDto) =>
    modal.confirm({
      title: t('role.deleteTitle'),
      content: t('role.deleteConfirm', { name: role.name }),
      okText: t('common.delete'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: async () => {
        try {
          await deleteRole(role.id)
          void message.success(t('role.deleted', { name: role.name }))
          if (selectedId === role.id) setSelectedId(null)
          await queryClient.invalidateQueries({ queryKey: qk.roles })
        } catch (error) {
          showApiError(error)
        }
      },
    })

  return (
    <>
      <PageHeader
        title={t('menu.roles')}
        description={t('role.pageHint')}
        extra={
          <Space wrap>
            <Button icon={<TableOutlined />} onClick={() => navigate('/admin/permission-matrix')}>
              {t('menu.permissionMatrix')}
            </Button>
            {can('role.upload') && (
              <Button type="primary" icon={<PlusOutlined />} onClick={() => setForm({ open: true, editing: null })}>
                {t('role.create')}
              </Button>
            )}
          </Space>
        }
      />
      <Row gutter={[16, 16]}>
        <Col xs={24} lg={8} xl={7}>
          <Card size="small" title={t('role.listTitle')}>
            <Input.Search
              allowClear
              placeholder={t('role.searchPlaceholder')}
              onChange={(e) => setKeyword(e.target.value)}
              style={{ marginBottom: 12 }}
            />
            {roles.isLoading ? (
              <Skeleton active />
            ) : (
              <List<RoleDto>
                dataSource={visible}
                locale={{ emptyText: <Empty description={term ? t('common.noMatch') : t('empty.roles')} /> }}
                renderItem={(r) => (
                  <List.Item
                    className={r.id === selected?.id ? 'bd-select-item bd-select-item-active' : 'bd-select-item'}
                    onClick={() => setSelectedId(r.id)}
                    actions={[
                      can('role.edit') && (
                        <Tooltip title={t('common.edit')} key="edit">
                          <Button
                            size="small"
                            type="text"
                            icon={<EditOutlined />}
                            aria-label={t('common.edit')}
                            onClick={(e) => {
                              e.stopPropagation()
                              setForm({ open: true, editing: r })
                            }}
                          />
                        </Tooltip>
                      ),
                      can('role.delete') && !r.isSystem && (
                        <Tooltip title={t('common.delete')} key="delete">
                          <Button
                            size="small"
                            type="text"
                            danger
                            icon={<DeleteOutlined />}
                            aria-label={t('common.delete')}
                            onClick={(e) => {
                              e.stopPropagation()
                              confirmDelete(r)
                            }}
                          />
                        </Tooltip>
                      ),
                    ].filter(Boolean)}
                  >
                    <List.Item.Meta
                      title={
                        <Space size={4} wrap>
                          <Typography.Text strong>{r.name}</Typography.Text>
                          {r.isSystem && (
                            <Tag icon={<LockOutlined />} color="gold">
                              {t('role.system')}
                            </Tag>
                          )}
                        </Space>
                      }
                      description={
                        <Space size={12} wrap>
                          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                            {r.code}
                          </Typography.Text>
                          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                            <TeamOutlined /> {t('role.userCount', { count: r.userCount })}
                          </Typography.Text>
                          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                            {t('role.permissionCount', { count: r.permissionCount })}
                          </Typography.Text>
                        </Space>
                      }
                    />
                  </List.Item>
                )}
              />
            )}
          </Card>
        </Col>
        <Col xs={24} lg={16} xl={17}>
          <PermissionMatrixEditor role={selected} />
        </Col>
      </Row>
      <RoleFormModal
        open={form.open}
        editing={form.editing}
        onClose={() => setForm({ open: false, editing: null })}
        onSaved={(id) => setSelectedId(id)}
      />
    </>
  )
}
