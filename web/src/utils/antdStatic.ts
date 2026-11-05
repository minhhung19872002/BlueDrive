import { App, Modal, message as staticMessage } from 'antd'

type AppApi = ReturnType<typeof App.useApp>

let appApi: AppApi | null = null

/** Called by <AntdAppBridge/> so non-React code can use context-aware antd message/notification/modal. */
export function setAntdApp(api: AppApi): void {
  appApi = api
}

export function getMessage(): AppApi['message'] {
  return appApi?.message ?? staticMessage
}

/** Confirm dialog (context-aware when the app is mounted). */
export function getModal(): { confirm: typeof Modal.confirm } {
  return appApi?.modal ? { confirm: (props) => appApi!.modal.confirm(props) } : Modal
}
