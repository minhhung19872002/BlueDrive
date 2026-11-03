import {
  Badge,
  Button,
  Dropdown,
  Input,
  Segmented,
  Select,
  Space,
  Tooltip,
  type MenuProps,
} from "antd";
import {
  AppstoreOutlined,
  BarsOutlined,
  CloseOutlined,
  CloudUploadOutlined,
  CopyOutlined,
  DeleteOutlined,
  DownloadOutlined,
  EditOutlined,
  FolderAddOutlined,
  FolderOutlined,
  InfoCircleOutlined,
  LinkOutlined,
  PlusOutlined,
  ScissorOutlined,
  ShareAltOutlined,
  SnippetsOutlined,
  UploadOutlined,
} from "@ant-design/icons";
import { useTranslation } from "react-i18next";
import { MEDIA_TYPES, type MediaType } from "@/types/drive";
import type { NewMenuExtension, ViewMode } from "./types";

interface ExplorerToolbarProps {
  isMobile: boolean;
  canUpload: boolean;
  canEdit: boolean;
  canDownload: boolean;
  canDelete: boolean;
  selectedCount: number;
  selectedFileCount: number;
  /** Number of clipboard items that can be pasted here (0 = nothing to paste). */
  pasteCount: number;
  search: string;
  onSearch: (q: string) => void;
  mediaType: MediaType | undefined;
  onMediaType: (m: MediaType | undefined) => void;
  sort: string | undefined;
  onSort: (s: string | undefined) => void;
  view: ViewMode;
  onView: (v: ViewMode) => void;
  infoOpen: boolean;
  onToggleInfo: () => void;
  onNewFolder: () => void;
  onUploadFiles: () => void;
  onUploadFolder: () => void;
  extraNewMenuItems?: NewMenuExtension[];
  onExtra: (ext: NewMenuExtension) => void;
  onDownload: () => void;
  /** Single selection only (like SharePoint's Share / Copy link in the command bar). */
  onShare?: () => void;
  onCopyLink?: () => void;
  onCopyTo: () => void;
  onMoveTo: () => void;
  onDelete: () => void;
  onBulkEdit: () => void;
  onPaste: () => void;
  onClearSelection: () => void;
}

const SORTS = ["name", "-name", "-updatedAt", "updatedAt", "-size", "size"];

export default function ExplorerToolbar(p: ExplorerToolbarProps) {
  const { t } = useTranslation();

  const newItems: NonNullable<MenuProps["items"]> = [
    {
      key: "folder",
      icon: <FolderAddOutlined />,
      label: t("header.newFolder"),
    },
    { type: "divider" },
    {
      key: "upload-files",
      icon: <UploadOutlined />,
      label: t("drive.uploadFiles"),
    },
    {
      key: "upload-folder",
      icon: <FolderOutlined />,
      label: t("drive.uploadFolder"),
    },
  ];
  if (p.extraNewMenuItems?.length) {
    newItems.push({ type: "divider" });
    p.extraNewMenuItems.forEach((ext) =>
      newItems.push({
        key: `ext:${ext.key}`,
        icon: ext.icon,
        label: ext.label,
      }),
    );
  }
  const onNew: MenuProps["onClick"] = ({ key }) => {
    if (key === "folder") p.onNewFolder();
    else if (key === "upload-files") p.onUploadFiles();
    else if (key === "upload-folder") p.onUploadFolder();
    else {
      const ext = p.extraNewMenuItems?.find((e) => `ext:${e.key}` === key);
      if (ext) p.onExtra(ext);
    }
  };

  const hasSelection = p.selectedCount > 0;
  const label = (text: string) => (p.isMobile ? undefined : text);

  return (
    <div className="bd-explorer-toolbar">
      <Space wrap size={8} className="bd-explorer-toolbar-left">
        {p.canUpload && !hasSelection && (
          <>
            <Dropdown
              menu={{ items: newItems, onClick: onNew }}
              trigger={["click"]}
            >
              <Button type="primary" icon={<PlusOutlined />}>
                {t("header.new")}
              </Button>
            </Dropdown>
            <Button icon={<CloudUploadOutlined />} onClick={p.onUploadFiles}>
              {label(t("header.upload"))}
            </Button>
          </>
        )}
        {p.pasteCount > 0 && !hasSelection && (
          <Tooltip title={t("drive.pasteHint")}>
            <Button icon={<SnippetsOutlined />} onClick={p.onPaste}>
              {t("drive.paste", { count: p.pasteCount })}
            </Button>
          </Tooltip>
        )}
        {hasSelection && (
          <>
            <Badge count={p.selectedCount} color="#0b5394">
              <Button icon={<CloseOutlined />} onClick={p.onClearSelection}>
                {label(t("common.clearSelection"))}
              </Button>
            </Badge>
            {p.onShare && (
              <Button icon={<ShareAltOutlined />} onClick={p.onShare}>
                {label(t("actions.share"))}
              </Button>
            )}
            {p.onCopyLink && (
              <Button icon={<LinkOutlined />} onClick={p.onCopyLink}>
                {label(t("actions.copyLink"))}
              </Button>
            )}
            {p.canDownload && (
              <Tooltip title={t("actions.download")}>
                <Button icon={<DownloadOutlined />} onClick={p.onDownload}>
                  {label(t("actions.download"))}
                </Button>
              </Tooltip>
            )}
            <Tooltip title={t("actions.copyTo")}>
              <Button icon={<CopyOutlined />} onClick={p.onCopyTo} />
            </Tooltip>
            {p.canEdit && (
              <Tooltip title={t("actions.moveTo")}>
                <Button icon={<ScissorOutlined />} onClick={p.onMoveTo} />
              </Tooltip>
            )}
            {p.canEdit && p.selectedFileCount > 1 && (
              <Tooltip title={t("drive.bulkEdit")}>
                <Button icon={<EditOutlined />} onClick={p.onBulkEdit} />
              </Tooltip>
            )}
            {p.canDelete && (
              <Tooltip title={t("actions.delete")}>
                <Button danger icon={<DeleteOutlined />} onClick={p.onDelete} />
              </Tooltip>
            )}
          </>
        )}
      </Space>
      <Space wrap size={8} className="bd-explorer-toolbar-right">
        {/* While items are selected the command bar shows their actions (like SharePoint): hiding the
            search / filters keeps the toolbar on one line, so the listing does not jump under the cursor. */}
        {!hasSelection && (
          <>
            <Input.Search
              allowClear
              defaultValue={p.search}
              placeholder={t("drive.searchInFolder")}
              onSearch={(v) => p.onSearch(v.trim())}
              style={{ width: p.isMobile ? "100%" : 220 }}
            />
            <Select<MediaType | undefined>
              allowClear
              value={p.mediaType}
              onChange={p.onMediaType}
              placeholder={t("drive.allTypes")}
              style={{ width: 150 }}
              options={MEDIA_TYPES.map((m) => ({
                value: m,
                label: t(`mediaType.${m}`),
              }))}
            />
            {p.view === "grid" && (
              <Select<string | undefined>
                value={p.sort ?? "name"}
                onChange={(v) => p.onSort(v === "name" ? undefined : v)}
                style={{ width: 170 }}
                options={SORTS.map((s) => ({
                  value: s,
                  label: t(`drive.sort.${s.replace("-", "desc_")}`),
                }))}
              />
            )}
          </>
        )}
        <Segmented<ViewMode>
          value={p.view}
          onChange={p.onView}
          options={[
            {
              value: "list",
              icon: <BarsOutlined />,
              title: t("drive.viewList"),
            },
            {
              value: "grid",
              icon: <AppstoreOutlined />,
              title: t("drive.viewGrid"),
            },
          ]}
        />
        <Tooltip title={p.infoOpen ? t("drive.hideInfo") : t("drive.showInfo")}>
          <Button
            type={p.infoOpen ? "primary" : "default"}
            ghost={p.infoOpen}
            icon={<InfoCircleOutlined />}
            onClick={p.onToggleInfo}
            aria-label={t("actions.info")}
          />
        </Tooltip>
      </Space>
    </div>
  );
}
