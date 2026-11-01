import i18n from '@/i18n'
import { getMessage } from './antdStatic'

/**
 * Copy text to the clipboard and show a Vietnamese confirmation. Falls back to execCommand when the
 * async Clipboard API is unavailable (plain-HTTP intranet deployments are not a secure context).
 */
export async function copyText(text: string): Promise<boolean> {
  let ok = false
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text)
      ok = true
    }
  } catch {
    ok = false
  }
  if (!ok) {
    const area = document.createElement('textarea')
    area.value = text
    area.setAttribute('readonly', '')
    area.style.position = 'fixed'
    area.style.opacity = '0'
    document.body.appendChild(area)
    area.select()
    try {
      ok = document.execCommand('copy')
    } catch {
      ok = false
    }
    area.remove()
  }
  if (ok) void getMessage().success(i18n.t('common.copied'))
  else void getMessage().error(i18n.t('common.copyFailed'))
  return ok
}
