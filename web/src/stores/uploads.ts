import { create } from 'zustand'
import { Upload, type DetailedError } from 'tus-js-client'
import { refreshSession } from '@/api/client'
import { completeUpload } from '@/api/drive'
import { getErrorMessage } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import i18n from '@/i18n'
import { queryClient } from '@/queryClient'
import { useAuthStore } from '@/stores/auth'
import type { PickedFile } from '@/features/drive/upload/filePicker'
import type { DriveTarget, UploadResult } from '@/types/drive'

export const TUS_ENDPOINT = '/tus'
const CHUNK_SIZE = 50 * 1024 * 1024
const MAX_PARALLEL = 3
const PROGRESS_THROTTLE_MS = 250

export type UploadStatus = 'queued' | 'uploading' | 'paused' | 'completing' | 'done' | 'error' | 'canceled'

/** Metadata entered once for a whole batch (#39 "áp dụng hàng loạt"). */
export interface BatchMetadata {
  title?: string | null
  description?: string | null
  tags?: string[]
  catalogItemIds?: string[]
  /** Upload as a new version of this existing file (#26/#43) instead of creating a file. */
  targetFileId?: string | null
  changeNote?: string | null
}

export interface UploadTask {
  id: string
  name: string
  relativePath: string | null
  size: number
  target: DriveTarget
  meta: BatchMetadata
  status: UploadStatus
  uploaded: number
  /** Bytes per second (smoothed). */
  speed: number
  error: string | null
  result: UploadResult | null
}

/** Files picked or dropped, waiting for the optional metadata dialog. */
export interface PendingBatch {
  files: PickedFile[]
  target: DriveTarget
  targetName: string
}

interface UploadState {
  tasks: UploadTask[]
  pendingBatch: PendingBatch | null
  collapsed: boolean
  /** Show the metadata dialog for these files (no-op for an empty list). */
  requestUpload: (files: PickedFile[], target: DriveTarget, targetName: string) => void
  cancelBatch: () => void
  confirmBatch: (meta: BatchMetadata) => void
  /** Queue files directly with the given metadata, without the metadata dialog (e.g. new version upload). */
  enqueue: (files: PickedFile[], target: DriveTarget, meta: BatchMetadata) => void
  pause: (id: string) => void
  resume: (id: string) => void
  cancel: (id: string) => void
  retry: (id: string) => void
  cancelAll: () => void
  clearFinished: () => void
  setCollapsed: (collapsed: boolean) => void
}

// Non-serializable runtime objects live outside the store.
const files = new Map<string, File>()
const uploads = new Map<string, Upload>()
const progressMarks = new Map<string, { bytes: number; time: number; emitted: number }>()
let pendingRefresh: Promise<string | null> | null = null
/** Completions run one at a time: concurrent folder uploads would otherwise race to create the same sub-folders. */
let completeChain: Promise<unknown> = Promise.resolve()
let seq = 0

const ACTIVE: UploadStatus[] = ['uploading', 'completing']

function patch(id: string, changes: Partial<UploadTask>) {
  useUploadStore.setState((s) => ({ tasks: s.tasks.map((t) => (t.id === id ? { ...t, ...changes } : t)) }))
}

function getTask(id: string): UploadTask | undefined {
  return useUploadStore.getState().tasks.find((t) => t.id === id)
}

/** Vietnamese message from a tus error: the server puts the reason (ValidateUpload) in the response body. */
function tusErrorMessage(error: Error | DetailedError): string {
  const t = i18n.t.bind(i18n)
  const response = 'originalResponse' in error ? error.originalResponse : null
  if (!response) return t('upload.errorNetwork')
  const status = response.getStatus()
  const body = (response.getBody() ?? '').trim()
  if (body && body.length < 1000 && !body.startsWith('<')) return body
  if (status === 401) return t('errors.status401')
  if (status === 403) return t('errors.status403')
  if (status === 413) return t('errors.status413')
  if (status === 415) return t('upload.errorType')
  if (status >= 500) return t('errors.status500')
  return t('errors.unknown')
}

function invalidateAfterUpload() {
  void queryClient.invalidateQueries({ queryKey: qk.drive })
  void queryClient.invalidateQueries({ queryKey: qk.workspaces })
  void queryClient.invalidateQueries({ queryKey: qk.me })
}

function finishComplete(id: string, tusId: string) {
  const task = getTask(id)
  if (!task) return
  patch(id, { status: 'completing', uploaded: task.size, speed: 0 })
  completeChain = completeChain.then(async () => {
    const current = getTask(id)
    if (!current || current.status !== 'completing') return
    try {
      const result = await completeUpload(tusId, {
        fileName: current.name,
        workspaceId: current.target.workspaceId,
        folderId: current.target.folderId,
        relativePath: current.relativePath,
        targetFileId: current.meta.targetFileId ?? null,
        title: current.meta.title || null,
        description: current.meta.description || null,
        tags: current.meta.tags?.length ? current.meta.tags : null,
        catalogItemIds: current.meta.catalogItemIds?.length ? current.meta.catalogItemIds : null,
        changeNote: current.meta.changeNote || null,
      })
      patch(id, { status: 'done', result, error: null })
      files.delete(id)
      uploads.delete(id)
      invalidateAfterUpload()
    } catch (error) {
      // The received bytes are discarded by the server on failure: a retry uploads again.
      uploads.delete(id)
      patch(id, { status: 'error', error: getErrorMessage(error) })
    } finally {
      pump()
    }
  })
}

function createUpload(task: UploadTask, file: File): Upload {
  const userId = useAuthStore.getState().user?.id ?? 'anon'
  const metadata: Record<string, string> = {
    filename: task.name,
    filetype: file.type || 'application/octet-stream',
    workspaceId: task.target.workspaceId,
  }
  if (task.target.folderId) metadata.folderId = task.target.folderId
  if (task.relativePath) metadata.relativePath = task.relativePath
  if (task.meta.title) metadata.title = task.meta.title
  if (task.meta.description) metadata.description = task.meta.description
  if (task.meta.tags?.length) metadata.tags = task.meta.tags.join(',')
  if (task.meta.catalogItemIds?.length) metadata.catalogItemIds = task.meta.catalogItemIds.join(',')
  if (task.meta.targetFileId) metadata.targetFileId = task.meta.targetFileId
  if (task.meta.changeNote) metadata.changeNote = task.meta.changeNote

  const upload: Upload = new Upload(file, {
    endpoint: TUS_ENDPOINT,
    chunkSize: CHUNK_SIZE,
    retryDelays: [0, 1000, 3000, 5000, 10000, 20000],
    metadata,
    removeFingerprintOnSuccess: true,
    storeFingerprintForResuming: true,
    // Fingerprint per user + destination so the same file sent to two folders never shares a tus session.
    fingerprint: async (f: File) =>
      [
        'bluedrive',
        userId,
        task.target.workspaceId,
        task.target.folderId ?? 'root',
        task.meta.targetFileId ?? 'new',
        task.relativePath ?? f.name,
        f.size,
        f.lastModified,
      ].join(':'),
    onBeforeRequest: async (req) => {
      if (pendingRefresh) await pendingRefresh
      const token = useAuthStore.getState().accessToken
      if (token) req.setHeader('Authorization', `Bearer ${token}`)
    },
    onShouldRetry: (err) => {
      const status = err.originalResponse?.getStatus() ?? 0
      if (status === 401) {
        pendingRefresh ??= refreshSession().finally(() => {
          pendingRefresh = null
        })
        return true
      }
      // Network errors, server errors and tus conflicts are retried; validation errors are final.
      return status === 0 || status === 409 || status === 423 || status >= 500
    },
    onProgress: (sent) => {
      const now = performance.now()
      const mark = progressMarks.get(task.id) ?? { bytes: sent, time: now, emitted: 0 }
      if (now - mark.emitted < PROGRESS_THROTTLE_MS) return
      const dt = (now - mark.time) / 1000
      const instant = dt > 0 ? (sent - mark.bytes) / dt : 0
      const prev = getTask(task.id)?.speed ?? 0
      const speed = prev > 0 ? prev * 0.7 + instant * 0.3 : instant
      progressMarks.set(task.id, { bytes: sent, time: now, emitted: now })
      patch(task.id, { uploaded: sent, speed: Math.max(0, speed) })
    },
    onSuccess: () => {
      progressMarks.delete(task.id)
      const tusId = upload.url?.split('/').filter(Boolean).pop()
      if (!tusId) {
        patch(task.id, { status: 'error', error: i18n.t('errors.unknown') })
        pump()
        return
      }
      finishComplete(task.id, tusId)
    },
    onError: (error) => {
      progressMarks.delete(task.id)
      if (getTask(task.id)?.status === 'canceled') return
      patch(task.id, { status: 'error', error: tusErrorMessage(error), speed: 0 })
      pump()
    },
  })
  return upload
}

async function startTask(id: string) {
  const task = getTask(id)
  const file = files.get(id)
  if (!task || !file) return
  patch(id, { status: 'uploading', error: null, speed: 0 })
  progressMarks.delete(id)
  let upload = uploads.get(id)
  if (!upload) {
    upload = createUpload(task, file)
    uploads.set(id, upload)
    try {
      // Resume an interrupted session for the same file and destination (e.g. after a page reload).
      const previous = await upload.findPreviousUploads()
      if (previous.length > 0) upload.resumeFromPreviousUpload(previous[0])
    } catch {
      // URL storage unavailable (private mode): start a fresh upload.
    }
  }
  if (getTask(id)?.status !== 'uploading') return
  upload.start()
}

/** Start queued tasks while fewer than MAX_PARALLEL transfers are running. */
function pump() {
  const { tasks } = useUploadStore.getState()
  let active = tasks.filter((t) => ACTIVE.includes(t.status)).length
  for (const t of tasks) {
    if (active >= MAX_PARALLEL) break
    if (t.status === 'queued') {
      active++
      void startTask(t.id)
    }
  }
}

export const useUploadStore = create<UploadState>()((set, get) => ({
  tasks: [],
  pendingBatch: null,
  collapsed: false,

  requestUpload: (picked, target, targetName) => {
    if (picked.length === 0) return
    set({ pendingBatch: { files: picked, target, targetName } })
  },

  cancelBatch: () => set({ pendingBatch: null }),

  confirmBatch: (meta) => {
    const batch = get().pendingBatch
    if (!batch) return
    set({ pendingBatch: null })
    get().enqueue(batch.files, batch.target, meta)
  },

  enqueue: (picked, target, meta) => {
    if (picked.length === 0) return
    const tasks: UploadTask[] = picked.map(({ file, relativePath }) => {
      const id = `u${Date.now().toString(36)}${(seq++).toString(36)}`
      files.set(id, file)
      return {
        id,
        name: file.name,
        relativePath,
        size: file.size,
        target,
        meta,
        status: 'queued',
        uploaded: 0,
        speed: 0,
        error: null,
        result: null,
      }
    })
    set((s) => ({ collapsed: false, tasks: [...s.tasks, ...tasks] }))
    pump()
  },

  pause: (id) => {
    const task = getTask(id)
    if (!task || (task.status !== 'uploading' && task.status !== 'queued')) return
    patch(id, { status: 'paused', speed: 0 })
    void uploads.get(id)?.abort(false)
    pump()
  },

  resume: (id) => {
    const task = getTask(id)
    if (!task || task.status !== 'paused') return
    patch(id, { status: 'queued' })
    pump()
  },

  cancel: (id) => {
    const task = getTask(id)
    if (!task || task.status === 'done' || task.status === 'canceled' || task.status === 'completing') return
    patch(id, { status: 'canceled', speed: 0 })
    const upload = uploads.get(id)
    uploads.delete(id)
    files.delete(id)
    // Terminate the server-side session so partial bytes are freed.
    if (upload) void upload.abort(!!upload.url).catch(() => undefined)
    pump()
  },

  retry: (id) => {
    const task = getTask(id)
    if (!task || task.status !== 'error' || !files.has(id)) return
    patch(id, { status: 'queued', error: null })
    pump()
  },

  cancelAll: () => {
    get()
      .tasks.filter((t) => t.status === 'queued' || t.status === 'uploading' || t.status === 'paused' || t.status === 'error')
      .forEach((t) => get().cancel(t.id))
  },

  clearFinished: () =>
    set((s) => {
      const keep = s.tasks.filter((t) => t.status !== 'done' && t.status !== 'canceled')
      s.tasks.filter((t) => !keep.includes(t)).forEach((t) => files.delete(t.id))
      return { tasks: keep }
    }),

  setCollapsed: (collapsed) => set({ collapsed }),
}))

/** Whether a task can still be retried (the File object is kept until it succeeds or is cancelled). */
export function canRetry(task: UploadTask): boolean {
  return task.status === 'error' && files.has(task.id)
}
