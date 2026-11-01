import Constants from 'expo-constants';
import { Platform } from 'react-native';

import { t } from '@/i18n';
import { useAuth } from '@/store/auth';
import type { AuthResult } from './types';

export const APP_VERSION = Constants.expoConfig?.version ?? '1.0.0';

/** Lets the server label the session device ("Ứng dụng BlueDrive trên Android/iOS"). */
export const USER_AGENT = (() => {
  if (Platform.OS === 'ios') {
    const device = (Platform as { isPad?: boolean }).isPad ? 'iPad' : 'iPhone';
    return `BlueDriveMobile/${APP_VERSION} (${device}; iOS ${Platform.Version})`;
  }
  if (Platform.OS === 'android') return `BlueDriveMobile/${APP_VERSION} (Android ${Platform.Version})`;
  return `BlueDriveMobile/${APP_VERSION} (${Platform.OS})`;
})();

const DEFAULT_TIMEOUT = 30_000;

export interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
  errors?: Record<string, string[]>;
}

/** Error carrying the Vietnamese ProblemDetails.detail returned by the server. */
export class ApiError extends Error {
  constructor(message: string, public status: number, public problem?: ProblemDetails) {
    super(message);
    this.name = 'ApiError';
  }
}

export function errorMessage(e: unknown): string {
  if (e instanceof ApiError) return e.message;
  if (e instanceof Error && e.message) return e.message;
  return t('common.error');
}

export function serverUrl(): string {
  return useAuth.getState().serverUrl;
}

/** Turns a relative server URL (e.g. signed `/api/v1/content/...`) into an absolute one. */
export function absoluteUrl(url: string | null | undefined): string | undefined {
  if (!url) return undefined;
  if (/^https?:\/\//i.test(url)) return url;
  return `${serverUrl()}${url.startsWith('/') ? '' : '/'}${url}`;
}

async function fetchWithTimeout(url: string, init: RequestInit, timeoutMs = DEFAULT_TIMEOUT): Promise<Response> {
  const controller = new AbortController();
  const outer = init.signal;
  const onAbort = () => controller.abort();
  outer?.addEventListener('abort', onAbort);
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    return await fetch(url, { ...init, signal: controller.signal });
  } catch (e) {
    if (outer?.aborted) throw e;
    if (controller.signal.aborted) throw new ApiError(t('common.timeoutError'), 0);
    throw new ApiError(t('common.networkError'), 0);
  } finally {
    clearTimeout(timer);
    outer?.removeEventListener('abort', onAbort);
  }
}

export async function parseError(res: Response): Promise<ApiError> {
  let problem: ProblemDetails | undefined;
  try {
    const text = await res.text();
    problem = text ? (JSON.parse(text) as ProblemDetails) : undefined;
  } catch {
    problem = undefined;
  }
  const fallback =
    res.status === 401 ? t('common.sessionExpired')
      : res.status >= 500 ? `${t('common.error')} (HTTP ${res.status})`
        : `${t('common.error')} (HTTP ${res.status})`;
  const fieldErrors = problem?.errors ? Object.values(problem.errors).flat().join(' ') : '';
  return new ApiError(problem?.detail || fieldErrors || problem?.title || fallback, res.status, problem);
}

function noteMustChangePassword(err: ApiError) {
  if (err.status === 403 && /đổi mật khẩu/i.test(err.message)) useAuth.getState().setMustChangePassword(true);
}

// ---------- Token refresh (single in-flight) ----------
let refreshing: Promise<string> | null = null;

class SessionExpiredError extends ApiError {}

async function doRefresh(): Promise<string> {
  const { refreshToken } = useAuth.getState();
  if (!refreshToken) throw new SessionExpiredError(t('common.sessionExpired'), 401);
  const res = await fetchWithTimeout(`${serverUrl()}/api/v1/auth/refresh`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', 'User-Agent': USER_AGENT },
    body: JSON.stringify({ refreshToken }),
  });
  if (!res.ok) {
    const err = await parseError(res);
    if (res.status === 401 || res.status === 400 || res.status === 403) {
      // Refresh token is dead (expired, revoked, idle timeout) → sign out locally.
      await useAuth.getState().clearSession();
      throw new SessionExpiredError(err.message || t('common.sessionExpired'), 401);
    }
    throw err;
  }
  const data = (await res.json()) as AuthResult;
  await useAuth.getState().setSession(data);
  return data.accessToken;
}

/** Rotates tokens; concurrent callers share the same request (refresh tokens are single-use). */
export function refreshTokens(): Promise<string> {
  if (!refreshing) {
    refreshing = doRefresh().finally(() => {
      refreshing = null;
    });
  }
  return refreshing;
}

/** Returns an access token valid for at least the next minute (refreshing if needed). */
export async function getValidAccessToken(minValidityMs = 60_000): Promise<string> {
  if (refreshing) return refreshing;
  const { accessToken, accessExpiresAt } = useAuth.getState();
  if (accessToken && accessExpiresAt - Date.now() > minValidityMs) return accessToken;
  return refreshTokens();
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
  body?: unknown;
  query?: Record<string, string | number | boolean | null | undefined>;
  auth?: boolean;
  signal?: AbortSignal;
  responseType?: 'json' | 'text' | 'none';
  timeoutMs?: number;
}

function buildUrl(path: string, query?: RequestOptions['query']): string {
  const base = `${serverUrl()}${path.startsWith('/api/') ? '' : '/api/v1'}${path}`;
  if (!query) return base;
  const qs = Object.entries(query)
    .filter(([, v]) => v !== undefined && v !== null && v !== '')
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`)
    .join('&');
  return qs ? `${base}?${qs}` : base;
}

/** JSON API call with bearer auth, transparent token refresh on 401 and Vietnamese error messages. */
export async function api<T = unknown>(path: string, opts: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, query, auth = true, signal, responseType = 'json', timeoutMs } = opts;
  if (!serverUrl()) throw new ApiError(t('login.serverRequired'), 0);
  const url = buildUrl(path, query);

  const send = async (token?: string) => {
    const headers: Record<string, string> = { Accept: 'application/json', 'User-Agent': USER_AGENT };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (token) headers.Authorization = `Bearer ${token}`;
    return fetchWithTimeout(url, { method, headers, body: body !== undefined ? JSON.stringify(body) : undefined, signal }, timeoutMs);
  };

  let res: Response;
  if (auth) {
    let token = await getValidAccessToken(15_000);
    res = await send(token);
    if (res.status === 401) {
      token = await refreshTokens();
      res = await send(token);
      if (res.status === 401) {
        await useAuth.getState().clearSession();
        throw new ApiError(t('common.sessionExpired'), 401);
      }
    }
  } else {
    res = await send();
  }

  if (!res.ok) {
    const err = await parseError(res);
    noteMustChangePassword(err);
    throw err;
  }
  if (responseType === 'none' || res.status === 204) return undefined as T;
  if (responseType === 'text') return (await res.text()) as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}
