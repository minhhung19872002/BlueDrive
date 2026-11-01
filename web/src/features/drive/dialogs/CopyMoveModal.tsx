import { useMemo, useState } from 'react'
import { Alert, Form, Modal, Select, TreeSelect } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { copyItems, getFolderTree, moveItems } from '@/api/drive'
import { qk } from '@/api/queryKeys'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useClearClipboardIfContains } from '../useClipboard'
import { useMyWorkspaces } from '../hooks'
import type { FolderTreeNode, ItemRef } from '@/types/drive'
import { filterTreeNodeUnaccent, type TreeSelectNode } from '@/utils/tree'

const ROOT = '__root__'

export interface CopyMoveRequest {
  mode: 'copy' | 'move'
  items: ItemRef[]
  /** Workspace of the items (default destination). */
  workspaceId: string
}

function toTree(nodes: FolderTreeNode[], blocked: Set<string>, parentBlocked = false): TreeSelectNode[] {
  return nodes.map((n) => {
    const off = parentBlocked || blocked.has(n.id)
    return {
      value: n.id,
      title: n.name,
      disabled: off || undefined,
      children: n.children.length ? toTree(n.children, blocked, off) : undefined,
    }
  })
}

/** "Sao chép tới..." / "Di chuyển tới...": pick a workspace, then a folder in its tree (#24). */
export default function CopyMoveModal({ request, onClose }: { request: CopyMoveRequest; onClose: () => void }) {
  const { t } = useTranslation()
  const workspaces = useMyWorkspaces()
  const [workspaceId, setWorkspaceId] = useState(request.workspaceId)
  const [folder, setFolder] = useState<string>(ROOT)
  const tree = useQuery({ queryKey: qk.folderTree(workspaceId), queryFn: () => getFolderTree(workspaceId) })
  const clearClipboard = useClearClipboardIfContains()

  // A folder cannot be moved into itself or one of its sub-folders.
  const blocked = useMemo(
    () => new Set(request.mode === 'move' ? request.items.filter((i) => i.type === 'folder').map((i) => i.id) : []),
    [request],
  )
  const treeData = useMemo<TreeSelectNode[]>(
    () => [
      {
        value: ROOT,
        title: t('drive.rootFolder'),
        children: toTree(tree.data ?? [], blocked),
      },
    ],
    [tree.data, blocked, t],
  )

  const isMove = request.mode === 'move'
  const run = useApiMutation(
    () => {
      const target = folder === ROOT ? null : folder
      return isMove ? moveItems(request.items, workspaceId, target).then((r) => r.moved) : copyItems(request.items, workspaceId, target).then((r) => r.copied)
    },
    {
      successMessage: (count) => (isMove ? t('drive.moved', { count }) : t('drive.copied', { count })),
      invalidate: [qk.drive, qk.workspaces, qk.me],
      onSuccess: () => {
        if (isMove) clearClipboard(request.items)
        onClose()
      },
    },
  )

  const options = (workspaces.data ?? []).map((w) => ({
    value: w.id,
    label: w.type === 'personal' ? t('menu.myDrive') : w.name,
    disabled: w.isLocked,
  }))

  return (
    <Modal
      open
      title={isMove ? t('drive.moveTitle', { count: request.items.length }) : t('drive.copyTitle', { count: request.items.length })}
      okText={isMove ? t('drive.moveHere') : t('drive.copyHere')}
      cancelText={t('common.cancel')}
      onOk={() => run.mutate()}
      onCancel={onClose}
      confirmLoading={run.isPending}
      destroyOnHidden
    >
      <Form layout="vertical">
        <Form.Item label={t('drive.destinationWorkspace')}>
          <Select
            value={workspaceId}
            loading={workspaces.isLoading}
            options={options}
            onChange={(v: string) => {
              setWorkspaceId(v)
              setFolder(ROOT)
            }}
            showSearch
            optionFilterProp="label"
          />
        </Form.Item>
        <Form.Item label={t('drive.destinationFolder')} extra={t('drive.destinationHint')}>
          <TreeSelect
            value={folder}
            onChange={(v: string) => setFolder(v ?? ROOT)}
            treeData={treeData}
            loading={tree.isLoading}
            treeDefaultExpandedKeys={[ROOT]}
            showSearch
            filterTreeNode={filterTreeNodeUnaccent}
            style={{ width: '100%' }}
            listHeight={320}
          />
        </Form.Item>
      </Form>
      {tree.isError && <Alert type="warning" showIcon message={t('drive.treeLoadFailed')} />}
    </Modal>
  )
}
