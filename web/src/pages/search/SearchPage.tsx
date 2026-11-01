import { useMemo, useState } from 'react'
import {
  App,
  Button,
  Card,
  Checkbox,
  Col,
  DatePicker,
  Dropdown,
  Empty,
  Form,
  Grid,
  Input,
  InputNumber,
  Modal,
  Pagination,
  Result,
  Row,
  Select,
  Space,
  Spin,
  TreeSelect,
  Typography,
  type MenuProps,
} from 'antd'
import { DeleteOutlined, FilterOutlined, SaveOutlined, SearchOutlined, StarOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import dayjs, { type Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { getErrorMessage } from '@/api/errors'
import { qkc } from '@/api/queryKeys.collab'
import { compactCriteria, deleteSavedSearch, listSavedSearches, saveSearch, searchFiles } from '@/api/search'
import { lookupUsers } from '@/api/users'
import PageHeader from '@/components/PageHeader'
import { useDriveActions } from '@/features/drive/actions/useDriveActions'
import { useCatalogOptions, useMyWorkspaces } from '@/features/drive/hooks'
import SearchHitList from '@/features/search/SearchHitList'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useOrgTree } from '@/hooks/useLookups'
import { MEDIA_TYPES, type MediaType } from '@/types/drive'
import type { SavedSearch, SearchCriteria } from '@/types/collab'
import type { CatalogItemDto } from '@/types/admin'
import { buildTree, filterTreeNodeUnaccent, orgTreeToSelect, type TreeOf } from '@/utils/tree'
import '@/styles/collab.css'

const MB = 1024 * 1024
const PAGE_SIZE = 20

const SORTS: { value: string; key: string }[] = [
  { value: 'relevance', key: 'relevance' },
  { value: '-createdAt', key: 'newest' },
  { value: 'createdAt', key: 'oldest' },
  { value: 'name', key: 'name' },
  { value: '-size', key: 'size' },
]

interface FormValues {
  q?: string
  mediaTypes?: MediaType[]
  extensions?: string[]
  /** catalogId → selected item ids */
  catalogs?: Record<string, string[] | undefined>
  organizationId?: string
  includeSubOrganizations?: boolean
  createdBy?: string
  range?: [Dayjs, Dayjs] | null
  minMb?: number | null
  maxMb?: number | null
  tag?: string
  scope?: string
}

interface CatalogNode {
  value: string
  title: string
  children?: CatalogNode[]
}

const toNodes = (items: TreeOf<CatalogItemDto>[]): CatalogNode[] =>
  items.map((i) => ({ value: i.id, title: i.name, children: i.children?.length ? toNodes(i.children) : undefined }))

function toCriteria(v: FormValues): SearchCriteria {
  return compactCriteria({
    q: v.q,
    mediaTypes: v.mediaTypes,
    extensions: v.extensions?.map((e) => e.trim().replace(/^\./, '').toLowerCase()).filter(Boolean),
    catalogItemIds: Object.values(v.catalogs ?? {}).flatMap((ids) => ids ?? []),
    organizationId: v.organizationId,
    includeSubOrganizations: v.organizationId ? (v.includeSubOrganizations ?? true) : undefined,
    createdBy: v.createdBy,
    from: v.range?.[0]?.format('YYYY-MM-DD'),
    to: v.range?.[1]?.format('YYYY-MM-DD'),
    minSize: v.minMb != null ? Math.round(v.minMb * MB) : undefined,
    maxSize: v.maxMb != null ? Math.round(v.maxMb * MB) : undefined,
    tag: v.tag,
    workspaceId: v.scope && v.scope !== 'all' ? v.scope : undefined,
  })
}

/** Rebuild the form from saved criteria (catalog items are regrouped by catalog). */
function toForm(c: SearchCriteria, itemCatalog: Map<string, string>): FormValues {
  const catalogs: Record<string, string[]> = {}
  for (const id of c.catalogItemIds ?? []) {
    const cat = itemCatalog.get(id)
    if (cat) (catalogs[cat] ??= []).push(id)
  }
  return {
    q: c.q ?? undefined,
    mediaTypes: c.mediaTypes ?? undefined,
    extensions: c.extensions ?? undefined,
    catalogs,
    organizationId: c.organizationId ?? undefined,
    includeSubOrganizations: c.includeSubOrganizations ?? true,
    createdBy: c.createdBy ?? undefined,
    range: c.from && c.to ? [dayjs(c.from), dayjs(c.to)] : null,
    minMb: c.minSize != null ? Math.round((c.minSize / MB) * 100) / 100 : null,
    maxMb: c.maxSize != null ? Math.round((c.maxSize / MB) * 100) / 100 : null,
    tag: c.tag ?? undefined,
    scope: c.workspaceId ?? 'all',
  }
}

/** Remote user picker (GET /users/lookup). */
function UserSelect({ value, onChange }: { value?: string; onChange?: (v?: string) => void }) {
  const { t } = useTranslation()
  const [q, setQ] = useState('')
  const [picked, setPicked] = useState<{ value: string; label: string } | null>(null)
  const users = useQuery({ queryKey: qkc.userLookup(q), queryFn: () => lookupUsers(q, 20), staleTime: 60_000 })
  const options = (users.data ?? []).map((u) => ({ value: u.id, label: `${u.fullName} (${u.username})` }))
  if (picked && value === picked.value && !options.some((o) => o.value === picked.value)) options.unshift(picked)
  return (
    <Select
      allowClear
      showSearch
      filterOption={false}
      value={value}
      onSearch={setQ}
      onChange={(v?: string) => {
        const o = options.find((x) => x.value === v)
        setPicked(o ?? null)
        onChange?.(v)
      }}
      loading={users.isFetching}
      options={options}
      placeholder={t('collab.search.uploaderPlaceholder')}
      notFoundContent={t('collab.discussion.noUser')}
    />
  )
}

function SearchInner({ initialQ }: { initialQ: string }) {
  const { t } = useTranslation()
  const { modal } = App.useApp()
  const screens = Grid.useBreakpoint()
  const isMobile = screens.md === false
  const [form] = Form.useForm<FormValues>()
  const [advanced, setAdvanced] = useState(!isMobile)
  const [criteria, setCriteria] = useState<SearchCriteria | null>(initialQ ? { q: initialQ } : null)
  const [sort, setSort] = useState<string>(initialQ ? 'relevance' : '-createdAt')
  const [page, setPage] = useState(1)
  const [saving, setSaving] = useState(false)
  const [saveName, setSaveName] = useState('')

  const catalogs = useCatalogOptions()
  const orgTree = useOrgTree(false)
  const workspaces = useMyWorkspaces()
  const saved = useQuery({ queryKey: qkc.savedSearches, queryFn: listSavedSearches })

  const request = criteria ? { ...criteria, sort, page, pageSize: PAGE_SIZE } : null
  const result = useQuery({
    queryKey: qkc.search(request ?? {}),
    queryFn: () => searchFiles(request!),
    enabled: !!request,
    placeholderData: keepPreviousData,
  })
  const hits = result.data?.items ?? []
  const actions = useDriveActions({ siblingFileIds: hits.map((h) => h.item.id) })

  const itemCatalog = useMemo(() => {
    const m = new Map<string, string>()
    for (const c of catalogs.data ?? []) for (const i of c.items) m.set(i.id, c.id)
    return m
  }, [catalogs.data])

  const catalogTrees = useMemo(
    () =>
      (catalogs.data ?? [])
        .filter((c) => c.items.length > 0)
        .map((c) => ({
          id: c.id,
          name: c.name,
          nodes: toNodes(buildTree(c.items, (a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name, 'vi'))),
        })),
    [catalogs.data],
  )

  const run = (values: FormValues) => {
    const c = toCriteria(values)
    setCriteria(c)
    setPage(1)
    if (!c.q && sort === 'relevance') setSort('-createdAt')
  }

  const save = useApiMutation((name: string) => saveSearch(name, { ...toCriteria(form.getFieldsValue()), sort }), {
    successMessage: (_, name) => t('collab.search.saved', { name }),
    invalidate: [qkc.savedSearches],
    onSuccess: () => {
      setSaving(false)
      setSaveName('')
    },
  })
  const removeSaved = useApiMutation((s: SavedSearch) => deleteSavedSearch(s.id), {
    successMessage: (_, s) => t('collab.search.savedDeleted', { name: s.name }),
    invalidate: [qkc.savedSearches],
  })

  const applySaved = (s: SavedSearch) => {
    const values = toForm(s.criteria, itemCatalog)
    form.resetFields()
    form.setFieldsValue(values)
    if (s.criteria.sort) setSort(s.criteria.sort)
    setAdvanced(true)
    run(values)
  }

  const savedMenu: MenuProps = {
    items: (saved.data ?? []).length
      ? (saved.data ?? []).map((s) => ({
          key: s.id,
          label: (
            <Space style={{ width: '100%', justifyContent: 'space-between' }}>
              <span>{s.name}</span>
              <Button
                type="text"
                size="small"
                danger
                icon={<DeleteOutlined />}
                aria-label={t('common.delete')}
                onClick={(e) => {
                  e.stopPropagation()
                  modal.confirm({
                    title: t('collab.search.deleteSavedTitle'),
                    content: t('collab.search.deleteSavedConfirm', { name: s.name }),
                    okText: t('common.delete'),
                    okButtonProps: { danger: true },
                    cancelText: t('common.cancel'),
                    onOk: () => removeSaved.mutateAsync(s).catch(() => undefined),
                  })
                }}
              />
            </Space>
          ),
        }))
      : [{ key: 'none', label: t('collab.search.noSaved'), disabled: true }],
    onClick: ({ key }) => {
      const s = saved.data?.find((x) => x.id === key)
      if (s) applySaved(s)
    },
  }

  const organizationId = Form.useWatch('organizationId', form)
  const hasKeyword = !!criteria?.q

  let results
  if (!criteria) results = <Empty description={t('empty.search')} style={{ padding: '40px 0' }} />
  else if (result.isError) results = <Result status="warning" title={t('collab.search.failed')} subTitle={getErrorMessage(result.error)} />
  else if (result.isLoading) results = <Spin style={{ display: 'block', padding: 48 }} />
  else
    results = (
      <Spin spinning={result.isFetching}>
        <SearchHitList
          hits={hits}
          actions={actions}
          empty={<Empty description={t('collab.search.noResults')} style={{ padding: '40px 0' }} />}
        />
      </Spin>
    )

  return (
    <>
      <PageHeader
        title={t('menu.search')}
        description={t('collab.search.description')}
        extra={
          <Dropdown menu={savedMenu} trigger={['click']}>
            <Button icon={<StarOutlined />} loading={saved.isLoading}>
              {t('collab.search.savedFilters')}
            </Button>
          </Dropdown>
        }
      />
      <div className="bd-search-layout">
        <Card size="small">
          <Form<FormValues>
            form={form}
            layout="vertical"
            initialValues={{ q: initialQ, includeSubOrganizations: true, scope: 'all' }}
            onFinish={run}
          >
            <Row gutter={12}>
              <Col xs={24} md={14}>
                <Form.Item name="q" label={t('collab.search.keyword')} rules={[{ max: 200, message: t('validation.maxLength', { max: 200 }) }]}>
                  <Input allowClear prefix={<SearchOutlined />} placeholder={t('collab.search.keywordPlaceholder')} maxLength={200} />
                </Form.Item>
              </Col>
              <Col xs={24} md={10}>
                <Form.Item name="scope" label={t('collab.search.scope')}>
                  <Select
                    loading={workspaces.isLoading}
                    showSearch
                    optionFilterProp="label"
                    options={[
                      { value: 'all', label: t('collab.search.scopeAll') },
                      ...(workspaces.data ?? []).map((w) => ({
                        value: w.id,
                        label: w.type === 'personal' ? t('menu.myDrive') : w.name,
                      })),
                    ]}
                  />
                </Form.Item>
              </Col>
            </Row>
            {advanced && (
              <>
                <Row gutter={12}>
                  <Col xs={24} md={12} lg={8}>
                    <Form.Item name="mediaTypes" label={t('collab.search.mediaTypes')}>
                      <Select
                        mode="multiple"
                        allowClear
                        maxTagCount="responsive"
                        placeholder={t('drive.allTypes')}
                        options={MEDIA_TYPES.map((m) => ({ value: m, label: t(`mediaType.${m}`) }))}
                      />
                    </Form.Item>
                  </Col>
                  <Col xs={24} md={12} lg={8}>
                    <Form.Item name="extensions" label={t('collab.search.extensions')}>
                      <Select mode="tags" tokenSeparators={[',', ' ']} open={false} placeholder={t('collab.search.extensionsPlaceholder')} />
                    </Form.Item>
                  </Col>
                  <Col xs={24} md={12} lg={8}>
                    <Form.Item name="tag" label={t('collab.search.tag')}>
                      <Input allowClear maxLength={100} placeholder={t('collab.search.tagPlaceholder')} />
                    </Form.Item>
                  </Col>
                  {catalogTrees.map((c) => (
                    <Col xs={24} md={12} lg={8} key={c.id}>
                      <Form.Item name={['catalogs', c.id]} label={c.name}>
                        <TreeSelect
                          multiple
                          allowClear
                          showSearch
                          treeData={c.nodes}
                          filterTreeNode={filterTreeNodeUnaccent}
                          maxTagCount="responsive"
                          placeholder={t('collab.search.catalogPlaceholder')}
                          listHeight={280}
                        />
                      </Form.Item>
                    </Col>
                  ))}
                  <Col xs={24} md={12} lg={8}>
                    <Form.Item name="organizationId" label={t('collab.search.organization')} style={{ marginBottom: 4 }}>
                      <TreeSelect
                        allowClear
                        showSearch
                        treeDefaultExpandAll
                        loading={orgTree.isLoading}
                        treeData={orgTreeToSelect(orgTree.data)}
                        filterTreeNode={filterTreeNodeUnaccent}
                        placeholder={t('collab.search.organizationPlaceholder')}
                        listHeight={300}
                      />
                    </Form.Item>
                    <Form.Item name="includeSubOrganizations" valuePropName="checked">
                      <Checkbox disabled={!organizationId}>{t('collab.search.includeSub')}</Checkbox>
                    </Form.Item>
                  </Col>
                  <Col xs={24} md={12} lg={8}>
                    <Form.Item name="createdBy" label={t('collab.search.uploader')}>
                      <UserSelect />
                    </Form.Item>
                  </Col>
                  <Col xs={24} md={12} lg={8}>
                    <Form.Item name="range" label={t('collab.search.dateRange')}>
                      <DatePicker.RangePicker
                        format="DD/MM/YYYY"
                        style={{ width: '100%' }}
                        placeholder={[t('drive.fromDate'), t('drive.toDate')]}
                      />
                    </Form.Item>
                  </Col>
                  <Col xs={24} md={12} lg={8}>
                    <Form.Item label={t('collab.search.sizeRange')} style={{ marginBottom: 0 }}>
                      <Space.Compact style={{ width: '100%' }}>
                        <Form.Item name="minMb" noStyle>
                          <InputNumber min={0} step={1} placeholder={t('collab.search.minMb')} style={{ width: '50%' }} />
                        </Form.Item>
                        <Form.Item
                          name="maxMb"
                          noStyle
                          dependencies={['minMb']}
                          rules={[
                            ({ getFieldValue }) => ({
                              validator: (_, v: number | null) => {
                                const min = getFieldValue('minMb') as number | null
                                return v != null && min != null && v < min
                                  ? Promise.reject(new Error(t('collab.search.sizeInvalid')))
                                  : Promise.resolve()
                              },
                            }),
                          ]}
                        >
                          <InputNumber min={0} step={1} placeholder={t('collab.search.maxMb')} style={{ width: '50%' }} />
                        </Form.Item>
                      </Space.Compact>
                    </Form.Item>
                  </Col>
                </Row>
              </>
            )}
            <Space wrap style={{ marginTop: advanced ? 12 : 0 }}>
              <Button type="primary" htmlType="submit" icon={<SearchOutlined />} loading={result.isFetching && page === 1}>
                {t('common.search')}
              </Button>
              <Button icon={<FilterOutlined />} onClick={() => setAdvanced((a) => !a)}>
                {advanced ? t('collab.search.hideAdvanced') : t('collab.search.showAdvanced')}
              </Button>
              <Button
                onClick={() => {
                  form.resetFields()
                  form.setFieldsValue({ q: '', includeSubOrganizations: true, scope: 'all' })
                  setCriteria(null)
                  setPage(1)
                }}
              >
                {t('common.reset')}
              </Button>
              <Button icon={<SaveOutlined />} onClick={() => setSaving(true)}>
                {t('collab.search.saveFilter')}
              </Button>
            </Space>
          </Form>
        </Card>

        <Card
          size="small"
          styles={{ body: { padding: 0 } }}
          title={
            criteria && result.data ? (
              <Typography.Text type="secondary" style={{ fontWeight: 400 }}>
                {t('collab.search.found', { count: result.data.total, ms: result.data.elapsedMs })}
              </Typography.Text>
            ) : (
              t('collab.search.results')
            )
          }
          extra={
            criteria && (
              <Select
                size="small"
                value={sort}
                onChange={(v) => {
                  setSort(v)
                  setPage(1)
                }}
                style={{ width: isMobile ? 130 : 170 }}
                options={SORTS.filter((s) => s.key !== 'relevance' || hasKeyword).map((s) => ({
                  value: s.value,
                  label: t(`collab.search.sort.${s.key}`),
                }))}
              />
            )
          }
        >
          {results}
          {(result.data?.total ?? 0) > PAGE_SIZE && (
            <Pagination
              style={{ padding: 12, textAlign: 'right' }}
              current={page}
              pageSize={PAGE_SIZE}
              total={result.data?.total ?? 0}
              showSizeChanger={false}
              size={isMobile ? 'small' : 'default'}
              onChange={(p) => {
                setPage(p)
                window.scrollTo({ top: 0, behavior: 'smooth' })
              }}
            />
          )}
        </Card>
      </div>

      <Modal
        open={saving}
        title={t('collab.search.saveTitle')}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        okButtonProps={{ disabled: !saveName.trim() }}
        confirmLoading={save.isPending}
        onOk={() => save.mutate(saveName.trim())}
        onCancel={() => setSaving(false)}
        destroyOnHidden
      >
        <Typography.Paragraph type="secondary">{t('collab.search.saveHint')}</Typography.Paragraph>
        <Input
          autoFocus
          value={saveName}
          maxLength={100}
          onChange={(e) => setSaveName(e.target.value)}
          onPressEnter={() => saveName.trim() && save.mutate(saveName.trim())}
          placeholder={t('collab.search.saveNamePlaceholder')}
        />
      </Modal>
      {actions.dialogs}
    </>
  )
}

/** Advanced search (#20, #32, #50–52). The header search box links here with ?q=. */
export default function SearchPage() {
  const [params] = useSearchParams()
  const q = params.get('q')?.trim() ?? ''
  // Remount on a new header query so the form and results start from it.
  return <SearchInner key={q} initialQ={q} />
}
