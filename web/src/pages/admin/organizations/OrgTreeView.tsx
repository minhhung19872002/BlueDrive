import { useMemo, useState } from 'react'
import { Alert, Card, Col, Empty, Input, Row, Skeleton, Space, Tag, Tree, Typography, type TreeDataNode, type TreeProps } from 'antd'
import { ApartmentOutlined, TeamOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { moveOrganization } from '@/api/organizations'
import { qk } from '@/api/queryKeys'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useOrgTree } from '@/hooks/useLookups'
import { useCan } from '@/hooks/usePermissions'
import type { OrganizationDto, OrganizationTreeNode } from '@/types/admin'
import { normalizeText } from '@/utils/tree'
import OrgDetailCard from './OrgDetailCard'

interface OrgTreeViewProps {
  onEdit: (org: OrganizationDto) => void
  onAddChild: (parentId: string) => void
}

interface IndexEntry {
  node: OrganizationTreeNode
  parentId: string | null
}

/** Index nodes by id with their actual parent in the tree (used to compute drop positions). */
function indexTree(nodes: OrganizationTreeNode[]): Map<string, IndexEntry> {
  const map = new Map<string, IndexEntry>()
  const walk = (list: OrganizationTreeNode[], parentId: string | null) =>
    list.forEach((n) => {
      map.set(n.id, { node: n, parentId })
      walk(n.children, n.id)
    })
  walk(nodes, null)
  return map
}

/** Keep nodes matching the keyword (accent-insensitive) plus their ancestors. */
function filterTree(nodes: OrganizationTreeNode[], term: string): OrganizationTreeNode[] {
  return nodes.flatMap((n) => {
    const children = filterTree(n.children, term)
    const hit = normalizeText(`${n.name} ${n.code}`).includes(term)
    return hit || children.length ? [{ ...n, children: hit && children.length === 0 ? n.children : children }] : []
  })
}

export default function OrgTreeView({ onEdit, onAddChild }: OrgTreeViewProps) {
  const { t } = useTranslation()
  const can = useCan()
  const tree = useOrgTree()
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [keyword, setKeyword] = useState('')
  const [expanded, setExpanded] = useState<string[] | null>(null)

  const term = normalizeText(keyword.trim())
  const nodes = useMemo(() => (term ? filterTree(tree.data ?? [], term) : (tree.data ?? [])), [tree.data, term])
  const index = useMemo(() => indexTree(tree.data ?? []), [tree.data])
  const allKeys = useMemo(() => [...indexTree(nodes).keys()], [nodes])

  const move = useApiMutation(
    (v: { id: string; parentId: string | null; index: number }) => moveOrganization(v.id, v.parentId, v.index),
    { successMessage: t('org.moved'), invalidate: [qk.organizations] },
  )

  const treeData = useMemo<TreeDataNode[]>(() => {
    const toData = (list: OrganizationTreeNode[]): TreeDataNode[] =>
      list.map((n) => ({
        key: n.id,
        title: (
          <Space size={6} wrap={false}>
            <Typography.Text delete={!n.isActive} type={n.isActive ? undefined : 'secondary'}>
              {n.name}
            </Typography.Text>
            <Tag bordered={false}>{n.code}</Tag>
            {n.type === 'phong_ban' && <Tag color="blue">{t('org.type.phong_ban')}</Tag>}
            {!n.isActive && <Tag>{t('status.inactive')}</Tag>}
            {n.userCount > 0 && (
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                <TeamOutlined /> {n.userCount}
              </Typography.Text>
            )}
          </Space>
        ),
        icon: <ApartmentOutlined />,
        children: n.children.length ? toData(n.children) : undefined,
      }))
    return toData(nodes)
  }, [nodes, t])

  const onDrop: TreeProps['onDrop'] = (info) => {
    const dragId = String(info.dragNode.key)
    const dropId = String(info.node.key)
    let parentId: string | null
    let newIndex: number
    if (!info.dropToGap) {
      // Dropped onto a node: becomes its first child.
      parentId = dropId
      newIndex = 0
    } else {
      const pos = info.node.pos.split('-')
      const relative = info.dropPosition - Number(pos[pos.length - 1])
      parentId = index.get(dropId)?.parentId ?? null
      const siblings = (parentId ? (index.get(parentId)?.node.children ?? []) : (tree.data ?? [])).filter(
        (n) => n.id !== dragId,
      )
      const at = siblings.findIndex((n) => n.id === dropId)
      newIndex = relative === -1 ? Math.max(at, 0) : at + 1
    }
    if (parentId === dragId) return
    move.mutate({ id: dragId, parentId, index: newIndex })
  }

  const canDrag = can('org.edit') && !term

  return (
    <Row gutter={[16, 16]}>
      <Col xs={24} lg={12} xl={11}>
        <Card size="small" title={t('org.treeTitle')}>
          <Input.Search
            allowClear
            placeholder={t('org.treeSearch')}
            onChange={(e) => setKeyword(e.target.value)}
            style={{ marginBottom: 12 }}
          />
          {canDrag && (
            <Alert type="info" showIcon message={t('org.dragHint')} style={{ marginBottom: 12 }} banner />
          )}
          {tree.isLoading ? (
            <Skeleton active paragraph={{ rows: 8 }} />
          ) : treeData.length === 0 ? (
            <Empty description={term ? t('common.noMatch') : t('empty.organizations')} />
          ) : (
            <div className="bd-org-tree">
              <Tree
                showIcon
                blockNode
                draggable={canDrag ? { icon: false } : false}
                treeData={treeData}
                expandedKeys={term ? allKeys : (expanded ?? allKeys)}
                onExpand={(keys) => setExpanded(keys.map(String))}
                selectedKeys={selectedId ? [selectedId] : []}
                onSelect={(keys) => setSelectedId(keys.length ? String(keys[0]) : null)}
                onDrop={onDrop}
                disabled={move.isPending}
              />
            </div>
          )}
        </Card>
      </Col>
      <Col xs={24} lg={12} xl={13}>
        {selectedId ? (
          <OrgDetailCard
            id={selectedId}
            onEdit={onEdit}
            onAddChild={onAddChild}
            onDeleted={() => setSelectedId(null)}
          />
        ) : (
          <Card size="small">
            <Empty description={t('org.selectNodeHint')} />
          </Card>
        )}
      </Col>
    </Row>
  )
}
