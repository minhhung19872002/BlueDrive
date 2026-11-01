import { api } from './client'
import type { AdminDashboard, GroupDashboard, PersonalDashboard } from '@/types/ops'

/** #63–66: personal dashboard (server cache 1 minute). */
export async function getPersonalDashboard(): Promise<PersonalDashboard> {
  const { data } = await api.get<PersonalDashboard>('/dashboards/personal')
  return data
}

/** #67–70: dashboard of the groups the user belongs to / leads. */
export async function getGroupDashboard(): Promise<GroupDashboard> {
  const { data } = await api.get<GroupDashboard>('/dashboards/group')
  return data
}

/** System-wide dashboard (perm dashboard.view). */
export async function getAdminDashboard(): Promise<AdminDashboard> {
  const { data } = await api.get<AdminDashboard>('/dashboards/admin')
  return data
}
