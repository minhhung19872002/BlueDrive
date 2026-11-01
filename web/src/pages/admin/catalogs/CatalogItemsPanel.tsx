import { useMemo, useState } from 'react'
import {
  Alert,
  App,
  Button,
  Card,
  Col,
  Empty,
  Input,
  List,
  Modal,
  Row,
  Select,
  Space,
  Switch,
  Table,
  Tooltip,
  Upload,
} from 'antd'
import { DeleteOutlined, EditOutlined, PlusOutlined, UploadOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import { useTranslation } from 'react-i18next'
import {
  deleteCatalogItem,
  exportCatalogItems,
  importCatalogItems,
  listCatalogItems,
  updateCatalogItem,
  type CatalogItemFilter,
} from '@/api/catalogs'
import { showApiError } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import ExportButtons from '@/components/ExportButtons'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useCan } from '@/hooks/usePermissions'
import type { CatalogDto, CatalogImportResult, CatalogItemDto } from '@/types/admin'
import { buildTree, type TreeOf } from '@/utils/tree'
import CatalogItemFormModal from './CatalogItemFormModal'

type ItemNode = TreeOf<CatalogItemDto>

interface FormState {
  open: boolean
  editing: CatalogItemDto | null
  parentId?: string | null
}

/** Items of one catalog as a tree table with CRUD, active toggle, Excel import/export. */
export default function CatalogItemsPanel({ catalog }: { catalog: CatalogDto }) {
  const { t } = useTranslation()
  const can = useCan()
  const { modal, message } = App.useApp()
  const queryClient = useQueryClient()
  const [filter, setFilter] = useState<CatalogItemFilter>({})
  const [form, setForm] = useState<FormState>({ open: false, editing: null })
  const [expanded, setExpanded] = useState<string[] | null>(null)
  const [importing, setImporting] = useState(false)
  const [importResult, setImportResult] = useState<CatalogImportResult | null>(null)

  const itemsKey = qk.catalogItems(catalog.id)
  const all = useQuery({ queryKey: [...itemsKey, {}], queryFn: () => listCatalogItems(catalog.id, {}) })
  const filtered = useQuery({
    queryKey: [...itemsKey, filter],
    queryFn: () => listCatalogItems(catalog.id, filter),
    placeholderData: keepPreviousData,
  })

  const tree = useMemo(
    () => buildTree(filtered.data ?? [], (a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name, 'vi')),
    [filtered.data],
  )
  const allKeys = useMemo(() => (filtered.data ?? []).map((i) => i.id), [filtered.data])

  const toggleActive = useApiMutation(
    (item: CatalogItemDto) =>
      updateCatalogItem(catalog.id, item.id, {
        parentId: item.parentId,
        code: item.code,
        name: item.name,
        sortOrder: item.sortOrder,
        isActive: !item.isActive,
      }),
    {
      successMessage: (_, item) => (item.isActive ? t('catalog.itemDisabled') : t('catalog.itemEnabled')),
      invalidate: [qk.catalogs],
    },
  )

  const confirmDelete = (item: CatalogItemDto) =>
    modal.confirm({
      title: t('catalog.itemDeleteTitle'),
      content: t('catalog.itemDeleteConfirm', { name: item.name }),
      okText: t('common.delete'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: async () => {
        try {
          await deleteCatalogItem(catalog.id, item.id)
          void message.success(t('catalog.itemDeleted', { name: item.name }))
          await queryClient.invalidateQueries({ queryKey: qk.catalogs })
        } catch (error) {
          showApiError(error)
        }
      },
    })

  const runImport = async (file: File) => {
    setImporting(true)
    try {
      const result = await importCatalogItems(catalog.id, file)
      setImportResult(result)
      await queryClient.invalidateQueries({ queryKey: qk.catalogs })
    } catch (error) {
      showApiError(error)
    } finally {
      setImporting(false)
    }
  }

  const columns: ColumnsType<ItemNode> = [
    { title: t('catalog.itemCode'), dataIndex: 'code', width: 180 },
    { title: t('catalog.itemName'), dataIndex: 'name' },
    { title: t('common.sortOrder'), dataIndex: 'sortOrder', width: 90, align: 'right' },
    { title: t('catalog.fileCount'), dataIndex: 'fileCount', width: 100, align: 'right' },
    {
      title: t('common.status'),
      dataIndex: 'isActive',
      width: 130,
      render: (v: boolean, item) => (
        <Switch
          size="small"
          checked={v}
          disabled={!can('catalog.edit')}
          loading={toggleActive.isPending && toggleActive.variables?.id === item.id}
          checkedChildren={t('catalog.inUse')}
          unCheckedChildren={t('catalog.notInUse')}
          onChange={() => toggleActive.mutate(item)}
        />
      ),
    },
    {
      title: t('common.actions'),
      key: 'actions',
      fixed: 'right',
      width: 120,
      render: (_, item) => (
        <Space size={4}>
          {can('catalog.upload') && (
            <Tooltip title={t('catalog.addChild')}>
              <Button
                size="small"
                type="text"
                icon={<PlusOutlined />}
                aria-label={t('catalog.addChild')}
                onClick={() => setForm({ open: true, editing: null, parentId: item.id })}
              />
            </Tooltip>
          )}
          {can('catalog.edit') && (
            <Tooltip title={t('common.edit')}>
              <Button
                size="small"
                type="text"
                icon={<EditOutlined />}
                aria-label={t('common.edit')}
                onClick={() => setForm({ open: true, editing: item })}
              />
            </Tooltip>
          )}
          {can('catalog.delete') && (
            <Tooltip title={t('common.delete')}>
              <Button size="small" type="text" danger icon={<DeleteOutlined />} aria-label={t('common.delete')} onClick={() => confirmDelete(item)} />
            </Tooltip>
          )}
        </Space>
      ),
    },
  ]

  return (
    <Card
      size="small"
      title={t('catalog.itemsTitle', { name: catalog.name })}
      extra={
        <Space wrap>
          <ExportButtons onExport={(format) => exportCatalogItems(catalog.id, format)} />
          {can('catalog.upload') && (
            <Upload
              accept=".xlsx"
              showUploadList={false}
              beforeUpload={(file) => {
                void runImport(file)
                return false
              }}
            >
              <Tooltip title={t('catalog.importHint')}>
                <Button icon={<UploadOutlined />} loading={importing}>
                  {t('catalog.import')}
                </Button>
              </Tooltip>
            </Upload>
          )}
          {can('catalog.upload') && (
            <Button type="primary" icon={<PlusOutlined />} onClick={() => setForm({ open: true, editing: null })}>
              {t('catalog.itemCreate')}
            </Button>
          )}
        </Space>
      }
    >
      {catalog.description && <Alert type="info" message={catalog.description} style={{ marginBottom: 12 }} />}
      <Row gutter={[12, 12]} style={{ marginBottom: 12 }}>
        <Col xs={24} md={14}>
          <Input.Search
            allowClear
            placeholder={t('catalog.itemSearch')}
            onSearch={(q) => setFilter((f) => ({ ...f, q: q || undefined }))}
          />
        </Col>
        <Col xs={24} md={10}>
          <Select
            allowClear
            style={{ width: '100%' }}
            placeholder={t('common.status')}
            value={filter.isActive === undefined ? undefined : String(filter.isActive)}
            onChange={(v?: string) => setFilter((f) => ({ ...f, isActive: v === undefined ? undefined : v === 'true' }))}
            options={[
              { value: 'true', label: t('catalog.inUse') },
              { value: 'false', label: t('catalog.notInUse') },
            ]}
          />
        </Col>
      </Row>
      <Table<ItemNode>
        rowKey="id"
        size="small"
        columns={columns}
        dataSource={tree}
        loading={filtered.isFetching}
        pagination={false}
        scroll={{ x: 'max-content' }}
        expandable={{
          expandedRowKeys: filter.q ? allKeys : (expanded ?? allKeys),
          onExpandedRowsChange: (keys) => setExpanded(keys.map(String)),
        }}
        locale={{ emptyText: <Empty description={filter.q || filter.isActive !== undefined ? t('common.noMatch') : t('empty.catalogItems')} /> }}
      />
      <CatalogItemFormModal
        open={form.open}
        catalogId={catalog.id}
        items={all.data ?? []}
        editing={form.editing}
        defaultParentId={form.parentId}
        onClose={() => setForm({ open: false, editing: null })}
      />
      <Modal
        open={importResult !== null}
        title={t('catalog.importResultTitle')}
        onCancel={() => setImportResult(null)}
        onOk={() => setImportResult(null)}
        okText={t('common.close')}
        cancelButtonProps={{ style: { display: 'none' } }}
      >
        {importResult && (
          <Space direction="vertical" style={{ width: '100%' }}>
            <Alert
              type={importResult.errors.length ? 'warning' : 'success'}
              showIcon
              message={t('catalog.importSummary', { created: importResult.created, updated: importResult.updated })}
            />
            {importResult.errors.length > 0 && (
              <List
                size="small"
                bordered
                header={t('catalog.importErrors', { count: importResult.errors.length })}
                dataSource={importResult.errors}
                renderItem={(e) => <List.Item>{e}</List.Item>}
                style={{ maxHeight: 320, overflow: 'auto' }}
              />
            )}
          </Space>
        )}
      </Modal>
    </Card>
  )
}
