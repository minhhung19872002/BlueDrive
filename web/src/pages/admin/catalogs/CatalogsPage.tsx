import { useState } from 'react'
import { App, Button, Card, Col, Empty, Input, List, Row, Skeleton, Space, Tag, Tooltip, Typography } from 'antd'
import { DeleteOutlined, EditOutlined, LockOutlined, PlusOutlined } from '@ant-design/icons'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { deleteCatalog, listCatalogs } from '@/api/catalogs'
import { showApiError } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import PageHeader from '@/components/PageHeader'
import { useCan } from '@/hooks/usePermissions'
import type { CatalogDto } from '@/types/admin'
import { normalizeText } from '@/utils/tree'
import CatalogFormModal from './CatalogFormModal'
import CatalogItemsPanel from './CatalogItemsPanel'

/** HSMT #10: shared catalogs (types on the left, hierarchical items on the right). */
export default function CatalogsPage() {
  const { t } = useTranslation()
  const can = useCan()
  const { modal, message } = App.useApp()
  const queryClient = useQueryClient()
  const [keyword, setKeyword] = useState('')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [form, setForm] = useState<{ open: boolean; editing: CatalogDto | null }>({ open: false, editing: null })

  const catalogs = useQuery({ queryKey: [...qk.catalogs, 'list'], queryFn: () => listCatalogs() })
  const term = normalizeText(keyword.trim())
  const visible = (catalogs.data ?? []).filter((c) => !term || normalizeText(`${c.name} ${c.code}`).includes(term))
  const selected = catalogs.data?.find((c) => c.id === selectedId) ?? catalogs.data?.[0]

  const confirmDelete = (c: CatalogDto) =>
    modal.confirm({
      title: t('catalog.deleteTitle'),
      content: t('catalog.deleteConfirm', { name: c.name, count: c.itemCount }),
      okText: t('common.delete'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: async () => {
        try {
          await deleteCatalog(c.id)
          void message.success(t('catalog.deleted', { name: c.name }))
          if (selectedId === c.id) setSelectedId(null)
          await queryClient.invalidateQueries({ queryKey: qk.catalogs })
        } catch (error) {
          showApiError(error)
        }
      },
    })

  return (
    <>
      <PageHeader
        title={t('menu.catalogs')}
        description={t('catalog.pageHint')}
        extra={
          can('catalog.upload') && (
            <Button type="primary" icon={<PlusOutlined />} onClick={() => setForm({ open: true, editing: null })}>
              {t('catalog.create')}
            </Button>
          )
        }
      />
      <Row gutter={[16, 16]}>
        <Col xs={24} lg={8} xl={6}>
          <Card size="small" title={t('catalog.typesTitle')}>
            <Input.Search
              allowClear
              placeholder={t('catalog.searchPlaceholder')}
              onChange={(e) => setKeyword(e.target.value)}
              style={{ marginBottom: 12 }}
            />
            {catalogs.isLoading ? (
              <Skeleton active />
            ) : (
              <List<CatalogDto>
                dataSource={visible}
                locale={{ emptyText: <Empty description={term ? t('common.noMatch') : t('empty.catalogs')} /> }}
                renderItem={(c) => (
                  <List.Item
                    className={c.id === selected?.id ? 'bd-select-item bd-select-item-active' : 'bd-select-item'}
                    onClick={() => setSelectedId(c.id)}
                    actions={[
                      can('catalog.edit') && (
                        <Tooltip title={t('common.edit')} key="edit">
                          <Button
                            size="small"
                            type="text"
                            icon={<EditOutlined />}
                            aria-label={t('common.edit')}
                            onClick={(e) => {
                              e.stopPropagation()
                              setForm({ open: true, editing: c })
                            }}
                          />
                        </Tooltip>
                      ),
                      can('catalog.delete') && !c.isSystem && (
                        <Tooltip title={t('common.delete')} key="delete">
                          <Button
                            size="small"
                            type="text"
                            danger
                            icon={<DeleteOutlined />}
                            aria-label={t('common.delete')}
                            onClick={(e) => {
                              e.stopPropagation()
                              confirmDelete(c)
                            }}
                          />
                        </Tooltip>
                      ),
                    ].filter(Boolean)}
                  >
                    <List.Item.Meta
                      title={
                        <Space size={4} wrap>
                          <Typography.Text strong>{c.name}</Typography.Text>
                          {c.isSystem && (
                            <Tag icon={<LockOutlined />} color="gold">
                              {t('catalog.system')}
                            </Tag>
                          )}
                        </Space>
                      }
                      description={
                        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                          {c.code} · {t('catalog.itemCount', { count: c.itemCount })}
                        </Typography.Text>
                      }
                    />
                  </List.Item>
                )}
              />
            )}
          </Card>
        </Col>
        <Col xs={24} lg={16} xl={18}>
          {selected ? (
            <CatalogItemsPanel key={selected.id} catalog={selected} />
          ) : (
            <Card size="small">
              <Empty description={catalogs.isLoading ? t('common.loading') : t('empty.catalogs')} />
            </Card>
          )}
        </Col>
      </Row>
      <CatalogFormModal
        open={form.open}
        editing={form.editing}
        onClose={() => setForm({ open: false, editing: null })}
        onSaved={(id) => setSelectedId(id)}
      />
    </>
  )
}
