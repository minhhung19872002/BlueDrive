import { App, message as staticMessage } from 'antd'

type AppApi = ReturnType<typeof App.useApp>

let appApi: AppApi | null = null

/** Called by <AntdAppBridge/> so non-React code can use context-aware antd message/notification/modal. */
export function setAntdApp(api: AppApi): void {
  appApi = api
}

export function getMessage(): AppApi['message'] {
  return appApi?.message ?? staticMessage
}
