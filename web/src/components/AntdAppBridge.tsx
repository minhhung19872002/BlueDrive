import { useEffect } from 'react'
import { App } from 'antd'
import { setAntdApp } from '@/utils/antdStatic'

/** Exposes antd App context (message/notification/modal) to non-React modules. */
export default function AntdAppBridge() {
  const app = App.useApp()
  useEffect(() => {
    setAntdApp(app)
  }, [app])
  return null
}
