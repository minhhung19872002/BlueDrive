import { isAxiosError } from 'axios'
import type { FormInstance } from 'antd'
import type { ProblemDetails } from './errors'

/**
 * Map ProblemDetails.errors ({ field: [messages] }) onto antd form fields.
 * Field names are camelCased (FluentValidation may return PascalCase property names).
 * Returns true when at least one field error was applied.
 */
export function applyFieldErrors(form: FormInstance, error: unknown, knownFields?: string[]): boolean {
  if (!isAxiosError(error)) return false
  const data = error.response?.data as ProblemDetails | undefined
  const errors = data?.errors
  if (!errors || Array.isArray(errors)) return false
  const fields = Object.entries(errors)
    .map(([key, value]) => ({
      name: key.charAt(0).toLowerCase() + key.slice(1),
      errors: (Array.isArray(value) ? value : [value]).filter((v): v is string => typeof v === 'string'),
    }))
    .filter((f) => f.name && f.errors.length && (!knownFields || knownFields.includes(f.name)))
  if (fields.length === 0) return false
  form.setFields(fields)
  return true
}

/** HTTP status of an API error, if any. */
export function errorStatus(error: unknown): number | undefined {
  return isAxiosError(error) ? error.response?.status : undefined
}
