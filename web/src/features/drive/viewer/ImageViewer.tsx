import { useState, type ReactNode } from 'react'
import { Button, Space, Tooltip } from 'antd'
import {
  ExpandOutlined,
  RotateLeftOutlined,
  RotateRightOutlined,
  ZoomInOutlined,
  ZoomOutOutlined,
} from '@ant-design/icons'
import { useTranslation } from 'react-i18next'

const STEPS = [0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 6]

/** Image lightbox with zoom (buttons / double click) and rotation. */
export default function ImageViewer({ src, alt, extra }: { src: string; alt: string; extra?: ReactNode }) {
  const { t } = useTranslation()
  const [scale, setScale] = useState(1)
  const [rotate, setRotate] = useState(0)
  const zoom = (dir: 1 | -1) =>
    setScale((s) => {
      const i = STEPS.findIndex((x) => x >= s)
      const next = STEPS[Math.min(STEPS.length - 1, Math.max(0, (i < 0 ? STEPS.length - 1 : i) + dir))]
      return next
    })
  return (
    <div className="bd-image-viewer">
      <div className="bd-image-stage" onDoubleClick={() => setScale((s) => (s === 1 ? 2 : 1))}>
        <img
          src={src}
          alt={alt}
          draggable={false}
          style={{
            transform: `scale(${scale}) rotate(${rotate}deg)`,
            maxWidth: scale <= 1 ? '100%' : 'none',
            maxHeight: scale <= 1 ? '100%' : 'none',
          }}
        />
      </div>
      <Space className="bd-image-tools" size={4} wrap>
        <Tooltip title={t('viewer.zoomOut')}>
          <Button shape="circle" icon={<ZoomOutOutlined />} onClick={() => zoom(-1)} disabled={scale <= STEPS[0]} />
        </Tooltip>
        <span className="bd-image-scale">{Math.round(scale * 100)}%</span>
        <Tooltip title={t('viewer.zoomIn')}>
          <Button shape="circle" icon={<ZoomInOutlined />} onClick={() => zoom(1)} disabled={scale >= STEPS[STEPS.length - 1]} />
        </Tooltip>
        <Tooltip title={t('viewer.rotateLeft')}>
          <Button shape="circle" icon={<RotateLeftOutlined />} onClick={() => setRotate((r) => r - 90)} />
        </Tooltip>
        <Tooltip title={t('viewer.rotateRight')}>
          <Button shape="circle" icon={<RotateRightOutlined />} onClick={() => setRotate((r) => r + 90)} />
        </Tooltip>
        <Tooltip title={t('viewer.reset')}>
          <Button
            shape="circle"
            icon={<ExpandOutlined />}
            onClick={() => {
              setScale(1)
              setRotate(0)
            }}
          />
        </Tooltip>
        {extra}
      </Space>
    </div>
  )
}
