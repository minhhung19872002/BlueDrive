import { useCallback, useMemo, useState, type ReactNode } from 'react'
import { App, Avatar, Button, Empty, Result, Skeleton, Space, Tag, Tooltip, Typography } from 'antd'
import { DeleteOutlined, DownloadOutlined, EditOutlined, MessageOutlined, PaperClipOutlined } from '@ant-design/icons'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { addComment, deleteComment, editComment, getAttachmentUrl, listComments } from '@/api/comments'
import { startDownload } from '@/api/drive'
import { getErrorMessage, showApiError } from '@/api/errors'
import { qkc } from '@/api/queryKeys.collab'
import FileTypeIcon from '@/features/drive/components/FileTypeIcon'
import { useFileDetail } from '@/features/drive/hooks'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useFileHub } from '@/hooks/useFileHub'
import { useAuthStore } from '@/stores/auth'
import type { CommentAttachment, CommentDto, CommentEvent } from '@/types/collab'
import { formatBytes, formatDateTime, formatRelative } from '@/utils/format'
import CommentComposer, { type ComposerValue } from './CommentComposer'
import '@/styles/collab.css'

const MENTION = /(@[\p{L}\p{N}._-]+)/u

/** Plain text with @mentions emphasised (no HTML is ever injected). */
function CommentText({ text }: { text: string }) {
  const parts = text.split(new RegExp(MENTION.source, 'gu'))
  return (
    <div className="bd-comment-text">
      {parts.map((p, i) =>
        MENTION.test(p) && p.startsWith('@') ? (
          <span key={i} className="bd-comment-mention">
            {p}
          </span>
        ) : (
          <span key={i}>{p}</span>
        ),
      )}
    </div>
  )
}

function initials(name: string): string {
  const words = name.trim().split(/\s+/)
  return (words.length > 1 ? words[words.length - 1][0] : words[0]?.[0] ?? '?').toUpperCase()
}

/** Insert or merge a comment (own comments keep canEdit even when the broadcast copy says false). */
function upsert(list: CommentDto[], c: CommentDto, myId: string | undefined): CommentDto[] {
  const mine = !!myId && c.authorId === myId && !c.isDeleted
  const i = list.findIndex((x) => x.id === c.id)
  if (i < 0) return [...list, { ...c, canEdit: c.canEdit || mine }]
  const next = [...list]
  next[i] = { ...c, canEdit: c.canEdit || list[i].canEdit || mine }
  return next
}

interface DiscussionPanelProps {
  fileId: string
}

/** "Thảo luận" (#27, #38): threaded comments with @mentions, attachments and live updates. */
export default function DiscussionPanel({ fileId }: DiscussionPanelProps) {
  const { t } = useTranslation()
  const { modal } = App.useApp()
  const queryClient = useQueryClient()
  const myId = useAuthStore((s) => s.user?.id)
  const detail = useFileDetail(fileId)
  const canModerate = !!detail.data?.access.canManage
  const key = qkc.comments(fileId)
  const comments = useQuery({ queryKey: key, queryFn: () => listComments(fileId) })
  const [replyTo, setReplyTo] = useState<string | null>(null)
  const [editing, setEditing] = useState<string | null>(null)

  const setList = useCallback(
    (fn: (list: CommentDto[]) => CommentDto[]) => queryClient.setQueryData<CommentDto[]>(qkc.comments(fileId), (old) => fn(old ?? [])),
    [queryClient, fileId],
  )

  const onEvent = useCallback(
    (e: CommentEvent) => {
      if (e.action === 'deleted') setList((list) => list.map((c) => (c.id === e.id ? { ...c, isDeleted: true, content: '', attachments: [], canEdit: false } : c)))
      else if (e.comment.fileId === fileId) setList((list) => upsert(list, e.comment, myId))
    },
    [fileId, myId, setList],
  )
  useFileHub(fileId, onEvent)

  const add = useCallback(
    async (parentId: string | null, v: ComposerValue) => {
      try {
        const c = await addComment(fileId, {
          parentId,
          content: v.content,
          mentions: v.mentions.length ? v.mentions : null,
          attachmentFileIds: v.attachmentFileIds.length ? v.attachmentFileIds : null,
        })
        setList((list) => upsert(list, c, myId))
        setReplyTo(null)
      } catch (error) {
        showApiError(error)
        throw error
      }
    },
    [fileId, myId, setList],
  )

  const edit = useCallback(
    async (id: string, v: ComposerValue) => {
      try {
        const c = await editComment(id, v.content)
        setList((list) => upsert(list, c, myId))
        setEditing(null)
      } catch (error) {
        showApiError(error)
        throw error
      }
    },
    [myId, setList],
  )

  const remove = useApiMutation((id: string) => deleteComment(id), {
    successMessage: t('collab.discussion.deleted'),
    onSuccess: (_, id) => setList((list) => list.map((c) => (c.id === id ? { ...c, isDeleted: true, content: '', attachments: [], canEdit: false } : c))),
  })

  const confirmDelete = (c: CommentDto) =>
    modal.confirm({
      title: t('collab.discussion.deleteTitle'),
      content: t('collab.discussion.deleteConfirm'),
      okText: t('common.delete'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: () => remove.mutateAsync(c.id).catch(() => undefined),
    })

  const openAttachment = async (c: CommentDto, a: CommentAttachment, inline: boolean) => {
    try {
      const r = await getAttachmentUrl(c.id, a.fileId, inline)
      if (inline) window.open(r.url, '_blank', 'noopener')
      else startDownload(r.url)
    } catch (error) {
      showApiError(error)
    }
  }

  // Thread structure: children by parent id (orphans of a missing parent become roots).
  const { roots, children, shownIds } = useMemo(() => {
    const list = comments.data ?? []
    const ids = new Set(list.map((c) => c.id))
    const byParent = new Map<string, CommentDto[]>()
    const top: CommentDto[] = []
    for (const c of list) {
      if (c.parentId && ids.has(c.parentId)) {
        const arr = byParent.get(c.parentId) ?? []
        arr.push(c)
        byParent.set(c.parentId, arr)
      } else top.push(c)
    }
    // A deleted comment stays visible as a placeholder only while it has visible replies.
    const shown = new Set<string>()
    const mark = (c: CommentDto): boolean => {
      let any = false
      for (const r of byParent.get(c.id) ?? []) if (mark(r)) any = true
      const keep = !c.isDeleted || any
      if (keep) shown.add(c.id)
      return keep
    }
    top.forEach(mark)
    return { roots: top, children: byParent, shownIds: shown }
  }, [comments.data])
  const visible = (c: CommentDto) => shownIds.has(c.id)

  const renderComment = (c: CommentDto, depth: number): ReactNode => {
    if (!visible(c)) return null
    const replies = (children.get(c.id) ?? []).filter(visible)
    return (
      <div key={c.id}>
        <div className="bd-comment">
          <Avatar size={32} style={{ backgroundColor: c.isDeleted ? '#bfbfbf' : 'var(--bd-primary)', flexShrink: 0 }}>
            {initials(c.authorName || '?')}
          </Avatar>
          <div className="bd-comment-body">
            <div className="bd-comment-meta">
              <Typography.Text strong>{c.authorName || t('collab.discussion.unknownUser')}</Typography.Text>
              <Tooltip title={formatDateTime(c.createdAt)}>
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {formatRelative(c.createdAt)}
                </Typography.Text>
              </Tooltip>
              {c.isEdited && !c.isDeleted && (
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  ({t('collab.discussion.edited')})
                </Typography.Text>
              )}
            </div>
            {c.isDeleted ? (
              <Typography.Text type="secondary" italic>
                {t('collab.discussion.deletedPlaceholder')}
              </Typography.Text>
            ) : editing === c.id ? (
              <CommentComposer
                autoFocus
                initialContent={c.content}
                allowAttachments={false}
                submitText={t('common.save')}
                onSubmit={(v) => edit(c.id, v)}
                onCancel={() => setEditing(null)}
              />
            ) : (
              <>
                {c.content && <CommentText text={c.content} />}
                {c.attachments.length > 0 && (
                  <div className="bd-comment-attachments">
                    {c.attachments.map((a) => (
                      <Tag key={a.fileId} style={{ maxWidth: '100%', whiteSpace: 'normal', marginInlineEnd: 0 }}>
                        <Space size={4}>
                          <FileTypeIcon type="file" mediaType={a.mediaType} extension={a.name.split('.').pop()} size={14} />
                          <Typography.Link onClick={() => void openAttachment(c, a, true)}>{a.name}</Typography.Link>
                          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                            {formatBytes(a.sizeBytes)}
                          </Typography.Text>
                          <Tooltip title={t('actions.download')}>
                            <Button
                              type="text"
                              size="small"
                              icon={<DownloadOutlined />}
                              onClick={() => void openAttachment(c, a, false)}
                              aria-label={t('actions.download')}
                            />
                          </Tooltip>
                        </Space>
                      </Tag>
                    ))}
                  </div>
                )}
                <Space size={0} wrap>
                  <Button type="link" size="small" icon={<MessageOutlined />} onClick={() => setReplyTo(replyTo === c.id ? null : c.id)}>
                    {t('collab.discussion.reply')}
                  </Button>
                  {c.canEdit && (
                    <Button type="link" size="small" icon={<EditOutlined />} onClick={() => setEditing(c.id)}>
                      {t('common.edit')}
                    </Button>
                  )}
                  {(c.canEdit || canModerate) && (
                    <Button type="link" size="small" danger icon={<DeleteOutlined />} onClick={() => confirmDelete(c)}>
                      {t('common.delete')}
                    </Button>
                  )}
                </Space>
              </>
            )}
            {replyTo === c.id && !c.isDeleted && (
              <CommentComposer
                autoFocus
                placeholder={t('collab.discussion.replyPlaceholder', { name: c.authorName })}
                submitText={t('collab.discussion.reply')}
                onSubmit={(v) => add(c.id, v)}
                onCancel={() => setReplyTo(null)}
              />
            )}
          </div>
        </div>
        {replies.length > 0 && (
          <div className={depth < 4 ? 'bd-comment-replies' : undefined}>{replies.map((r) => renderComment(r, depth + 1))}</div>
        )}
      </div>
    )
  }

  let body: ReactNode
  if (comments.isLoading) body = <Skeleton active avatar />
  else if (comments.isError) body = <Result status="warning" subTitle={getErrorMessage(comments.error)} />
  else {
    const shown = roots.filter(visible)
    body =
      shown.length === 0 ? (
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('collab.discussion.empty')} />
      ) : (
        <div>{shown.map((c) => renderComment(c, 0))}</div>
      )
  }

  return (
    <div className="bd-discussion">
      <CommentComposer onSubmit={(v) => add(null, v)} />
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        <PaperClipOutlined /> {t('collab.discussion.hint')}
      </Typography.Text>
      {body}
    </div>
  )
}
