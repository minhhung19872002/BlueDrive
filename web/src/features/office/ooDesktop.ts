import { api } from '@/api/client'
import type { AuthUser } from '@/types/auth'

/**
 * ONLYOFFICE Desktop Editors integration (DMS provider "bluedrive"): BlueDrive runs inside the desktop app, which exposes
 * window.AscDesktopEditor.execCommand(command, jsonString) — portal:login / portal:logout / open:document / portal:uitheme.
 * See docs/onlyoffice-desktop-integration.md.
 */
export const OO_PROVIDER = 'bluedrive'

interface AscDesktopEditorApi {
  execCommand: (command: string, parameters: string) => void
}

function desktop(): AscDesktopEditorApi | null {
  const w = window as unknown as { AscDesktopEditor?: AscDesktopEditorApi }
  return typeof w.AscDesktopEditor?.execCommand === 'function' ? w.AscDesktopEditor : null
}

/** True when the page runs inside ONLYOFFICE Desktop Editors. */
export function isInDesktopEditors(): boolean {
  return desktop() !== null
}

/** Registers BlueDrive in "Connected clouds" (portal:login). */
export function ooLogin(user: AuthUser) {
  desktop()?.execCommand(
    'portal:login',
    JSON.stringify({
      displayName: user.fullName,
      email: user.email ?? '',
      domain: window.location.origin,
      provider: OO_PROVIDER,
      userId: user.id,
      uiTheme: 'theme-light',
    }),
  )
}

/** Removes BlueDrive from "Connected clouds" (portal:logout). */
export function ooLogout() {
  desktop()?.execCommand('portal:logout', JSON.stringify({ domain: window.location.origin }))
}

const TYPES: Record<string, string> = {
  doc: 'word', docx: 'word', odt: 'word', rtf: 'word', txt: 'word',
  xls: 'cell', xlsx: 'cell', ods: 'cell', csv: 'cell',
  ppt: 'slide', pptx: 'slide', odp: 'slide',
  pdf: 'pdf',
}

/** Opens a document as an editor tab of the desktop app (open:document); returns false outside Desktop Editors. */
export function ooOpenDocument(path: string, extension: string | null): boolean {
  const d = desktop()
  if (!d) return false
  const url = new URL(path, window.location.origin).toString()
  d.execCommand('open:document', JSON.stringify({ url, type: TYPES[(extension ?? '').toLowerCase()] ?? 'word', provider: OO_PROVIDER }))
  return true
}

export interface RedeemResult {
  auth: { accessToken: string; refreshToken: string; user: AuthUser }
  next: string
}

/** One-time ticket from BlueDrive Desktop → web session (no second sign-in). */
export async function redeemTicket(ticket: string): Promise<RedeemResult> {
  const { data } = await api.post<RedeemResult>('/onlyoffice-desktop/tickets/redeem', { ticket })
  return data
}
