/** GET /auth/me — UserProfileDto. */
export interface AuthUser {
  id: string
  username: string
  fullName: string
  email: string | null
  phone: string | null
  organizationId: string | null
  organizationName: string | null
  position: string | null
  avatarFileId: string | null
  roles: string[]
  permissions: string[]
  mustChangePassword: boolean
  quotaBytes: number
  usedBytes: number
  lastLoginAt: string | null
  personalWorkspaceId: string | null
}

export interface LoginRequest {
  username: string
  password: string
}

/** Response of login / refresh / change-password (AuthResult). */
export interface AuthResponse {
  accessToken: string
  accessTokenExpiresAt: string
  refreshToken: string
  refreshTokenExpiresAt: string
  user: AuthUser
}

export type LoginResponse = AuthResponse

export interface ChangePasswordRequest {
  currentPassword: string
  newPassword: string
  signOutOtherSessions: boolean
}

export interface UpdateProfileRequest {
  fullName: string
  email?: string | null
  phone?: string | null
  position?: string | null
}

export interface PasswordPolicy {
  minLength: number
  requireUpper: boolean
  requireLower: boolean
  requireDigit: boolean
  requireSpecial: boolean
  expiryDays: number
  historyCount: number
}

export interface Branding {
  appName?: string
  orgName?: string
  logoUrl?: string
}

export interface SessionDto {
  id: string
  userId: string
  username: string
  fullName: string
  device: string | null
  ip: string | null
  userAgent: string | null
  createdAt: string
  lastSeenAt: string
  revokedAt: string | null
  isCurrent: boolean
}
