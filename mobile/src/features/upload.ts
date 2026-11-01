import { Directory, File, Paths, UploadType } from 'expo-file-system';

import { ApiError, getValidAccessToken, refreshTokens, serverUrl, USER_AGENT, type ProblemDetails } from '@/api/client';
import type { UploadResult } from '@/api/types';
import { t } from '@/i18n';
import { invalidateData } from '@/store/query';
import { toast } from '@/store/toast';
import { newTransferId, useTransfers } from '@/store/transfers';

export const MAX_UPLOAD_BYTES = 512 * 1024 * 1024;

export interface UploadSource {
  uri: string;
  name?: string | null;
  mimeType?: string | null;
  size?: number | null;
}

const pad = (n: number) => String(n).padStart(2, '0');

function defaultName(src: UploadSource): string {
  const fromUri = decodeURIComponent(src.uri.split('?')[0].split('/').pop() ?? '');
  const ext = /\.([a-z0-9]{1,6})$/i.exec(fromUri)?.[1] ?? (src.mimeType?.startsWith('video/') ? 'mp4' : 'jpg');
  const d = new Date();
  const prefix = src.mimeType?.startsWith('video/') ? 'VID' : 'IMG';
  return `${prefix}_${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}_${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}.${ext}`;
}

function cleanName(name: string): string {
  return name.replace(/[\\/:*?"<>|\u0000-\u001f]/g, '_').trim();
}

/** The multipart filename comes from the local file name → copy to a temp file with the real name when needed. */
async function prepareFile(src: UploadSource, name: string): Promise<{ file: File; temp?: Directory }> {
  const original = new File(src.uri);
  if (original.name === name) return { file: original };
  const dir = new Directory(Paths.cache, 'uploads', newTransferId());
  dir.create({ intermediates: true, idempotent: true });
  const dest = new File(dir, name);
  await original.copy(dest);
  return { file: dest, temp: dir };
}

function parseBody(body: string): ProblemDetails & Partial<UploadResult> {
  try {
    return body ? JSON.parse(body) : {};
  } catch {
    return {};
  }
}

async function uploadOne(src: UploadSource, workspaceId: string, folderId: string | null, transferId: string, index: number, total: number) {
  const name = cleanName(src.name || defaultName(src));
  if (src.size && src.size > MAX_UPLOAD_BYTES) throw new ApiError(t('upload.tooLarge', { name }), 413);
  const { file, temp } = await prepareFile(src, name);
  const controller = new AbortController();
  const transfers = useTransfers.getState();
  transfers.update(transferId, { name, progress: 0, label: t('upload.uploading', { current: index + 1, total, name }), cancel: () => controller.abort() });
  try {
    const parameters: Record<string, string> = { workspaceId };
    if (folderId) parameters.folderId = folderId;
    const run = async (token: string) => {
      const task = file.createUploadTask(`${serverUrl()}/api/v1/files/upload`, {
        httpMethod: 'POST',
        uploadType: UploadType.MULTIPART,
        fieldName: 'file',
        mimeType: src.mimeType || 'application/octet-stream',
        parameters,
        headers: { Authorization: `Bearer ${token}`, 'User-Agent': USER_AGENT, Accept: 'application/json' },
        signal: controller.signal,
        onProgress: ({ bytesSent, totalBytes }) => {
          const p = totalBytes > 0 ? Math.min(1, bytesSent / totalBytes) : -1;
          useTransfers.getState().update(transferId, { progress: p });
        },
      });
      try {
        return await task.uploadAsync();
      } finally {
        task.release();
      }
    };
    let res = await run(await getValidAccessToken(5 * 60_000));
    if (res.status === 401) res = await run(await refreshTokens());
    const body = parseBody(res.body);
    if (res.status < 200 || res.status >= 300) {
      const fieldErrors = body.errors ? Object.values(body.errors).flat().join(' ') : '';
      throw new ApiError(body.detail || fieldErrors || body.title || `HTTP ${res.status}`, res.status, body);
    }
    return { name, result: body as UploadResult };
  } finally {
    if (temp) {
      try {
        temp.delete();
      } catch {
        // best effort
      }
    }
  }
}

let busy = false;

/** Uploads files sequentially to POST /api/v1/files/upload (multipart, up to 512 MB), with progress + cancel. */
export async function uploadFiles(sources: UploadSource[], workspaceId: string, folderId: string | null) {
  if (sources.length === 0) return;
  const transferId = newTransferId();
  useTransfers.getState().start({ id: transferId, kind: 'upload', name: '', progress: 0, label: '' });
  busy = true;
  let ok = 0;
  try {
    for (let i = 0; i < sources.length; i++) {
      const src = sources[i];
      try {
        const { name, result } = await uploadOne(src, workspaceId, folderId, transferId, i, sources.length);
        ok++;
        if (result.approvalStatus === 'pending') toast.info(t('upload.pendingApproval', { name }));
      } catch (e) {
        if ((e as Error)?.name === 'AbortError') {
          toast.info(t('upload.cancelled'));
          break;
        }
        const name = src.name || defaultName(src);
        toast.error(t('upload.failed', { name, error: e instanceof Error ? e.message : String(e) }));
      }
    }
  } finally {
    busy = false;
    useTransfers.getState().finish(transferId);
    if (ok > 0) {
      toast.success(t('upload.done', { count: ok }));
      await invalidateData();
    }
  }
}

export const isUploading = () => busy;
