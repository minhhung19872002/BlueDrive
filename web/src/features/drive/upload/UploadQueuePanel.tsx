import { useEffect } from 'react'
import { App, Button, Progress, Tag, Tooltip, Typography } from 'antd'
import {
  CaretRightOutlined,
  CheckCircleFilled,
  CloseCircleFilled,
  CloseOutlined,
  DownOutlined,
  EyeOutlined,
  PauseOutlined,
  RedoOutlined,
  StopOutlined,
  UpOutlined,
} from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { canRetry, useUploadStore, type UploadTask } from '@/stores/uploads'
import { formatBytes } from '@/utils/format'

function statusText(task: UploadTask, t: (k: string, o?: Record<string, unknown>) => string): string {
  switch (task.status) {
    case 'queued':
      return t('upload.status.queued')
    case 'uploading':
      return `${formatBytes(task.uploaded)} / ${formatBytes(task.size)}${task.speed > 0 ? ` · ${formatBytes(task.speed)}/s` : ''}`
    case 'paused':
      return t('upload.status.paused', { done: formatBytes(task.uploaded), total: formatBytes(task.size) })
    case 'completing':
      return t('upload.status.completing')
    case 'done':
      return task.result?.approvalStatus === 'pending' ? t('upload.status.donePending') : t('upload.status.done')
    case 'canceled':
      return t('upload.status.canceled')
    default:
      return ''
  }
}

function TaskRow({ task }: { task: UploadTask }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const { pause, resume, cancel, retry } = useUploadStore.getState()
  const percent = task.size > 0 ? Math.floor((task.uploaded / task.size) * 100) : task.status === 'done' ? 100 : 0
  const active = task.status === 'uploading' || task.status === 'queued' || task.status === 'paused'
  return (
    <div className="bd-upload-row">
      <div className="bd-upload-row-main">
        <Typography.Text ellipsis className="bd-upload-name" title={task.relativePath ?? task.name}>
          {task.status === 'done' && <CheckCircleFilled style={{ color: '#52c41a', marginInlineEnd: 6 }} />}
          {task.status === 'error' && <CloseCircleFilled style={{ color: '#ff4d4f', marginInlineEnd: 6 }} />}
          {task.relativePath ?? task.name}
        </Typography.Text>
        <div className="bd-upload-actions">
          {task.status === 'uploading' && (
            <Tooltip title={t('upload.pause')}>
              <Button size="small" type="text" icon={<PauseOutlined />} onClick={() => pause(task.id)} />
            </Tooltip>
          )}
          {task.status === 'paused' && (
            <Tooltip title={t('upload.resume')}>
              <Button size="small" type="text" icon={<CaretRightOutlined />} onClick={() => resume(task.id)} />
            </Tooltip>
          )}
          {canRetry(task) && (
            <Tooltip title={t('upload.retry')}>
              <Button size="small" type="text" icon={<RedoOutlined />} onClick={() => retry(task.id)} />
            </Tooltip>
          )}
          {task.status === 'done' && task.result && (
            <Tooltip title={t('upload.viewFile')}>
              <Button size="small" type="text" icon={<EyeOutlined />} onClick={() => navigate(`/files/${task.result!.fileId}`)} />
            </Tooltip>
          )}
          {(active || task.status === 'error') && (
            <Tooltip title={t('upload.cancel')}>
              <Button size="small" type="text" danger icon={<StopOutlined />} onClick={() => cancel(task.id)} />
            </Tooltip>
          )}
        </div>
      </div>
      {(task.status === 'uploading' || task.status === 'paused' || task.status === 'completing') && (
        <Progress
          percent={percent}
          size="small"
          status={task.status === 'paused' ? 'normal' : 'active'}
          showInfo={false}
          style={{ margin: 0 }}
        />
      )}
      {task.status === 'error' ? (
        <Typography.Text type="danger" className="bd-upload-status">
          {task.error}
        </Typography.Text>
      ) : (
        <Typography.Text type="secondary" className="bd-upload-status">
          {statusText(task, t)}
          {task.status === 'done' && task.result?.approvalStatus === 'pending' && (
            <Tag color="orange" style={{ marginInlineStart: 6 }}>
              {t('approval.status.pending')}
            </Tag>
          )}
        </Typography.Text>
      )}
    </div>
  )
}

/** Floating upload queue (bottom-right): progress, speed, pause/resume/cancel/retry, "Xem tệp". */
export default function UploadQueuePanel() {
  const { t } = useTranslation()
  const { modal } = App.useApp()
  const tasks = useUploadStore((s) => s.tasks)
  const collapsed = useUploadStore((s) => s.collapsed)
  const setCollapsed = useUploadStore((s) => s.setCollapsed)
  const running = tasks.filter((x) => ['queued', 'uploading', 'completing', 'paused'].includes(x.status)).length

  // Warn before leaving the page while files are still uploading.
  useEffect(() => {
    if (running === 0) return
    const handler = (e: BeforeUnloadEvent) => {
      e.preventDefault()
      e.returnValue = ''
    }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [running])

  if (tasks.length === 0) return null
  const done = tasks.filter((x) => x.status === 'done').length
  const failed = tasks.filter((x) => x.status === 'error').length
  const totalBytes = tasks.reduce((s, x) => s + (x.status === 'canceled' ? 0 : x.size), 0)
  const sentBytes = tasks.reduce((s, x) => s + (x.status === 'canceled' ? 0 : x.status === 'done' ? x.size : x.uploaded), 0)
  const title =
    running > 0
      ? t('upload.panelRunning', { count: running })
      : failed > 0
        ? t('upload.panelFailed', { done, failed })
        : t('upload.panelDone', { count: done })

  const close = () => {
    if (running === 0) {
      useUploadStore.getState().clearFinished()
      useUploadStore.setState({ tasks: useUploadStore.getState().tasks.filter((x) => x.status !== 'error') })
      return
    }
    modal.confirm({
      title: t('upload.cancelAllTitle'),
      content: t('upload.cancelAllConfirm', { count: running }),
      okText: t('upload.cancelAll'),
      okButtonProps: { danger: true },
      cancelText: t('upload.keepUploading'),
      onOk: () => {
        const store = useUploadStore.getState()
        store.cancelAll()
        store.clearFinished()
        useUploadStore.setState({ tasks: [] })
      },
    })
  }

  return (
    <div className="bd-upload-panel" role="region" aria-label={t('upload.panelLabel')}>
      <div className="bd-upload-head">
        <Typography.Text strong ellipsis style={{ color: '#fff', flex: 1 }}>
          {title}
        </Typography.Text>
        <Button
          type="text"
          size="small"
          icon={collapsed ? <UpOutlined /> : <DownOutlined />}
          onClick={() => setCollapsed(!collapsed)}
          aria-label={collapsed ? t('upload.expand') : t('upload.collapse')}
          style={{ color: '#fff' }}
        />
        <Button type="text" size="small" icon={<CloseOutlined />} onClick={close} aria-label={t('common.close')} style={{ color: '#fff' }} />
      </div>
      {running > 0 && (
        <Progress
          percent={totalBytes > 0 ? Math.floor((sentBytes / totalBytes) * 100) : 0}
          size="small"
          showInfo={false}
          style={{ margin: 0, lineHeight: 0 }}
          strokeLinecap="square"
        />
      )}
      {!collapsed && (
        <div className="bd-upload-list">
          {tasks.map((task) => (
            <TaskRow key={task.id} task={task} />
          ))}
        </div>
      )}
    </div>
  )
}
