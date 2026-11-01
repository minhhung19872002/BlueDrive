import { api } from './client'
import type { DownloadUrl, UploadResult } from '@/types/drive'
import type { AddCommentRequest, AttachmentListItem, CommentDto } from '@/types/collab'

export async function listComments(fileId: string): Promise<CommentDto[]> {
  const { data } = await api.get<CommentDto[]>(`/files/${fileId}/comments`)
  return data
}

export async function addComment(fileId: string, body: AddCommentRequest): Promise<CommentDto> {
  const { data } = await api.post<CommentDto>(`/files/${fileId}/comments`, body)
  return data
}

export async function editComment(id: string, content: string): Promise<CommentDto> {
  const { data } = await api.put<CommentDto>(`/comments/${id}`, { content })
  return data
}

export async function deleteComment(id: string): Promise<void> {
  await api.delete(`/comments/${id}`)
}

/** Signed URL of a comment attachment (inline = open in the browser, otherwise download). */
export async function getAttachmentUrl(commentId: string, fileId: string, inline: boolean): Promise<DownloadUrl> {
  const { data } = await api.get<DownloadUrl>(`/comments/${commentId}/attachments/${fileId}/url`, { params: { inline } })
  return data
}

/** Attachments posted in discussions of files I can see (#19). */
export async function listMyAttachments(take = 100): Promise<AttachmentListItem[]> {
  const { data } = await api.get<AttachmentListItem[]>('/comments/attachments', { params: { take } })
  return data
}

/** Multipart upload of a small file (comment attachment) into a workspace folder. */
export async function uploadSmallFile(
  file: File,
  workspaceId: string,
  folderId: string | null,
  onProgress?: (percent: number) => void,
): Promise<UploadResult> {
  const form = new FormData()
  form.append('file', file)
  form.append('workspaceId', workspaceId)
  if (folderId) form.append('folderId', folderId)
  const { data } = await api.post<UploadResult>('/files/upload', form, {
    onUploadProgress: (e) => {
      if (onProgress && e.total) onProgress(Math.round((e.loaded / e.total) * 100))
    },
  })
  return data
}
