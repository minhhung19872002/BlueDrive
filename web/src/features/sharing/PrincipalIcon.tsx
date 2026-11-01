import { ApartmentOutlined, SafetyCertificateOutlined, TeamOutlined, UserOutlined } from '@ant-design/icons'
import type { PrincipalType } from '@/types/sharing'

export default function PrincipalIcon({ type }: { type: PrincipalType }) {
  switch (type) {
    case 'group':
      return <TeamOutlined />
    case 'organization':
      return <ApartmentOutlined />
    case 'role':
      return <SafetyCertificateOutlined />
    default:
      return <UserOutlined />
  }
}
