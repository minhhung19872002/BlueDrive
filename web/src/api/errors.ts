import { isAxiosError } from 'axios'
import i18n from '@/i18n'
import { getMessage } from '@/utils/antdStatic'

/** RFC 7807 ProblemDetails as returned by ASP.NET Core. */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  errors?: Record<string, string[] | string> | string[]
  traceId?: string
}

const HANDLED_STATUSES = [400, 401, 403, 404, 409, 413, 422, 429, 500, 503]
/** Text containing non-ASCII characters is assumed to be Vietnamese (ASP.NET default titles are English). */
const NON_ASCII = /[^ -~]/

function isProblemDetails(data: unknown): data is ProblemDetails {
  return typeof data === 'object' && data !== null && ('title' in data || 'detail' in data || 'errors' in data)
}

function flattenValidationErrors(errors: ProblemDetails['errors']): string[] {
  if (!errors) return []
  const values = Array.isArray(errors) ? errors : Object.values(errors)
  return values
    .flatMap((v) => (Array.isArray(v) ? v : [v]))
    .filter((v): v is string => typeof v === 'string' && v.trim() !== '')
}

/**
 * Convert any API error into a Vietnamese user-facing message.
 * Priority: per-status override > detail > validation errors > Vietnamese title > status message > generic.
 */
export function getErrorMessage(error: unknown, overrides?: Partial<Record<number, string>>): string {
  const t = i18n.t.bind(i18n)
  if (!isAxiosError(error)) {
    return error instanceof Error && NON_ASCII.test(error.message) ? error.message : t('errors.unknown')
  }
  if (error.code === 'ECONNABORTED' || error.code === 'ETIMEDOUT') return t('errors.timeout')
  if (!error.response) return t('errors.network')

  const { status, data } = error.response
  const override = overrides?.[status]
  if (override) return override

  if (isProblemDetails(data)) {
    if (data.detail) return data.detail
    const validation = flattenValidationErrors(data.errors)
    if (validation.length > 0) return validation.join('\n')
    if (data.title && NON_ASCII.test(data.title)) return data.title
  }
  if (HANDLED_STATUSES.includes(status)) return t(`errors.status${status}`)
  if (status >= 500) return t('errors.status500')
  return t('errors.unknown')
}

/** Show an antd error message for the given API error. */
export function showApiError(error: unknown, overrides?: Partial<Record<number, string>>): void {
  void getMessage().error(getErrorMessage(error, overrides))
}
