export interface AuthUser {
  id: string
  username: string
  fullName: string
  permissions: string[]
  mustChangePassword: boolean
}

export interface LoginRequest {
  username: string
  password: string
}

/** Response of POST /auth/login (user always present) and POST /auth/refresh (user optional). */
export interface AuthResponse {
  accessToken: string
  refreshToken: string
  user?: AuthUser
}

export interface LoginResponse extends AuthResponse {
  user: AuthUser
}
