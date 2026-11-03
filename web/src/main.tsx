import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { isInDesktopEditors } from '@/features/office/ooDesktop'
import { App as AntApp, ConfigProvider } from 'antd'
import viVN from 'antd/locale/vi_VN'
import { QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'
import dayjs from 'dayjs'
import 'dayjs/locale/vi'
import 'antd/dist/reset.css'
import './styles/global.css'
import './styles/print.css'
import './i18n'
import AntdAppBridge from './components/AntdAppBridge'
import { queryClient } from './queryClient'
import AppRoutes from './routes'
import { theme } from './theme'

dayjs.locale('vi')

// Inside ONLYOFFICE Desktop Editors the app header is hidden (the desktop app has its own tabs and sign-out).
if (isInDesktopEditors()) document.documentElement.classList.add('bd-oo-desktop')

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ConfigProvider locale={viVN} theme={theme}>
      <AntApp>
        <AntdAppBridge />
        <QueryClientProvider client={queryClient}>
          <BrowserRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
            <AppRoutes />
          </BrowserRouter>
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>
  </StrictMode>,
)
