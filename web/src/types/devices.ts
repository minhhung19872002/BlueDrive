export type DeviceStatus = 'active' | 'revoked' | 'wipe_pending' | 'wiped'

/** A computer running BlueDrive Desktop. */
export interface DeviceDto {
  id: string
  userId: string
  userName: string
  userFullName: string
  name: string
  os: string
  appVersion: string
  status: DeviceStatus
  createdAt: string
  lastSeenAt: string
  lastIp: string | null
  revokedAt: string | null
  wipeRequestedAt: string | null
  wipedAt: string | null
  isCurrent: boolean
}

export interface DesktopAuthorizeRequest {
  clientId: string
  redirectUri: string
  codeChallenge: string
  codeChallengeMethod: string
  state: string | null
  deviceName: string | null
}
