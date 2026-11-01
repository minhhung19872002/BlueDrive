import { useMemo, useRef, useState } from 'react'
import { Button, Mentions, Progress, Space, Tag, Tooltip, Typography } from 'antd'
import { PaperClipOutlined, SendOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { uploadSmallFile } from '@/api/comments'
import { getErrorMessage } from '@/api/errors'
import { qkc } from '@/api/queryKeys.collab'
import { lookupUsers } from '@/api/users'
import { pickFiles } from '@/features/drive/upload/filePicker'
import { useAuthStore } from '@/stores/auth'
import type { UserLookupDto } from '@/types/admin'
import { getMessage } from '@/utils/antdStatic'
import { formatBytes } from '@/utils/format'

export interface ComposerValue {
  content: string
  mentions: string[]
  attachmentFileIds: string[]
}

interface Attachment {
  key: string
  name: string
  size: number
  progress: number
  fileId: string | null
  error: string | null
}

interface CommentComposerProps {
  /** Initial text (edit mode). */
  initialContent?: string
  placeholder?: string
  submitText?: string
  /** Edit mode: no attachments (the API only updates the text). */
  allowAttachments?: boolean
  autoFocus?: boolean
  onSubmit: (value: ComposerValue) => Promise<unknown>
  onCancel?: () => void
}

const MAX_ATTACHMENTS = 10
/** The multipart endpoint accepts up to 512 MB; larger files belong in the drive itself. */
const MAX_ATTACHMENT_BYTES = 512 * 1024 * 1024

/** Comment editor with @mention autocomplete (GET /users/lookup) and attachments uploaded to "Tài liệu của tôi". */
export default function CommentComposer({
  initialContent = '',
  placeholder,
  submitText,
  allowAttachments = true,
  autoFocus,
  onSubmit,
  onCancel,
}: CommentComposerProps) {
  const { t } = useTranslation()
  const personalWorkspaceId = useAuthStore((s) => s.user?.personalWorkspaceId)
  const [content, setContent] = useState(initialContent)
  const [search, setSearch] = useState<string | null>(null)
  const [attachments, setAttachments] = useState<Attachment[]>([])
  const [sending, setSending] = useState(false)
  /** username → user id for mentions chosen from the list. */
  const mentionMap = useRef(new Map<string, string>())
  const seq = useRef(0)

  const users = useQuery({
    queryKey: qkc.userLookup(search ?? ''),
    queryFn: () => lookupUsers(search ?? '', 10),
    enabled: search !== null,
    staleTime: 60_000,
  })
  const options = useMemo(
    () =>
      (users.data ?? []).map((u: UserLookupDto) => ({
        key: u.id,
        value: u.username,
        label: (
          <span>
            {u.fullName} <Typography.Text type="secondary">@{u.username}</Typography.Text>
            {u.organizationName && (
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {' '}
                · {u.organizationName}
              </Typography.Text>
            )}
          </span>
        ),
      })),
    [users.data],
  )

  const uploadingCount = attachments.filter((a) => !a.fileId && !a.error).length
  const ready = attachments.filter((a) => a.fileId)
  const canSend = !sending && uploadingCount === 0 && (content.trim().length > 0 || ready.length > 0)

  const patch = (key: string, changes: Partial<Attachment>) =>
    setAttachments((list) => list.map((a) => (a.key === key ? { ...a, ...changes } : a)))

  const attach = async () => {
    if (!personalWorkspaceId) {
      void getMessage().error(t('upload.noPersonalWorkspace'))
      return
    }
    const picked = await pickFiles(false)
    const room = MAX_ATTACHMENTS - attachments.length
    if (picked.length > room) void getMessage().warning(t('collab.discussion.maxAttachments', { max: MAX_ATTACHMENTS }))
    for (const { file } of picked.slice(0, Math.max(0, room))) {
      const key = `a${seq.current++}`
      const tooBig = file.size > MAX_ATTACHMENT_BYTES
      setAttachments((list) => [
        ...list,
        { key, name: file.name, size: file.size, progress: 0, fileId: null, error: tooBig ? t('collab.discussion.attachmentTooBig') : null },
      ])
      if (tooBig) continue
      uploadSmallFile(file, personalWorkspaceId, null, (p) => patch(key, { progress: p }))
        .then((r) => patch(key, { fileId: r.fileId, progress: 100 }))
        .catch((error) => patch(key, { error: getErrorMessage(error) }))
    }
  }

  const submit = async () => {
    if (!canSend) return
    const text = content.trim()
    // Keep only mentions still present in the text.
    const mentions = [...mentionMap.current.entries()].filter(([username]) => text.includes(`@${username}`)).map(([, id]) => id)
    setSending(true)
    try {
      await onSubmit({ content: text, mentions: [...new Set(mentions)], attachmentFileIds: ready.map((a) => a.fileId!) })
      setContent('')
      setAttachments([])
    } catch {
      // The caller shows the error; keep the draft.
    } finally {
      setSending(false)
    }
  }

  return (
    <div className="bd-composer">
      <Mentions
        autoFocus={autoFocus}
        value={content}
        onChange={setContent}
        onSearch={(text) => setSearch(text)}
        onSelect={(option) => {
          const u = users.data?.find((x) => x.username === option.value)
          if (u) mentionMap.current.set(u.username, u.id)
        }}
        options={options}
        loading={users.isFetching}
        notFoundContent={search ? t('collab.discussion.noUser') : t('collab.discussion.typeToSearch')}
        placeholder={placeholder ?? t('collab.discussion.placeholder')}
        autoSize={{ minRows: 2, maxRows: 8 }}
        maxLength={5000}
        filterOption={false}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
            e.preventDefault()
            void submit()
          }
        }}
      />
      {attachments.length > 0 && (
        <Space direction="vertical" size={4} style={{ width: '100%' }}>
          {attachments.map((a) => (
            <div key={a.key}>
              <Tag
                closable
                onClose={(e) => {
                  e.preventDefault()
                  setAttachments((list) => list.filter((x) => x.key !== a.key))
                }}
                color={a.error ? 'red' : a.fileId ? 'blue' : 'default'}
                style={{ maxWidth: '100%', whiteSpace: 'normal' }}
              >
                <PaperClipOutlined /> {a.name} ({formatBytes(a.size)})
              </Tag>
              {!a.fileId && !a.error && <Progress percent={a.progress} size="small" />}
              {a.error && (
                <Typography.Text type="danger" style={{ fontSize: 12, display: 'block' }}>
                  {a.error}
                </Typography.Text>
              )}
            </div>
          ))}
        </Space>
      )}
      <div className="bd-composer-actions">
        <Space size={4}>
          {allowAttachments && (
            <Tooltip title={t('collab.discussion.attachHint')}>
              <Button icon={<PaperClipOutlined />} onClick={() => void attach()} disabled={attachments.length >= MAX_ATTACHMENTS}>
                {t('collab.discussion.attach')}
              </Button>
            </Tooltip>
          )}
        </Space>
        <Space size={4}>
          {onCancel && <Button onClick={onCancel}>{t('common.cancel')}</Button>}
          <Button type="primary" icon={<SendOutlined />} disabled={!canSend} loading={sending} onClick={() => void submit()}>
            {submitText ?? t('collab.discussion.send')}
          </Button>
        </Space>
      </div>
    </div>
  )
}
