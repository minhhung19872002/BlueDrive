import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
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
