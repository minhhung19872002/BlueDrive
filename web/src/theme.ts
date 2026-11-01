import type { ThemeConfig } from 'antd'

export const PRIMARY_COLOR = '#0b5394'

export const FONT_FAMILY =
  "'Be Vietnam Pro', 'Segoe UI', Roboto, 'Noto Sans', 'Helvetica Neue', Arial, sans-serif"

export const theme: ThemeConfig = {
  token: {
    colorPrimary: PRIMARY_COLOR,
    colorInfo: PRIMARY_COLOR,
    colorLink: PRIMARY_COLOR,
    fontFamily: FONT_FAMILY,
    borderRadius: 6,
  },
  components: {
    Layout: {
      headerBg: '#ffffff',
      headerHeight: 64,
      headerPadding: '0 16px',
      siderBg: '#ffffff',
      bodyBg: '#f5f7fa',
    },
  },
}
