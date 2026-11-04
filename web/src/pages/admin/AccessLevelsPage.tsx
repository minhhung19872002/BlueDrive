import { useState } from "react";
import {
  Alert,
  Button,
  Checkbox,
  Input,
  Modal,
  Popconfirm,
  Space,
  Table,
  Tag,
  Tooltip,
} from "antd";
import {
  DeleteOutlined,
  EditOutlined,
  PlusOutlined,
  UndoOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import {
  deletePermissionLevel,
  listAccessRights,
  listPermissionLevels,
  resetPermissionLevel,
  savePermissionLevel,
  type AccessRightInfo,
  type PermissionLevel,
} from "@/api/permissions";
import PageHeader from "@/components/PageHeader";
import { useApiMutation } from "@/hooks/useApiMutation";
import { useCan } from "@/hooks/usePermissions";

/** Spec 3.2 / 11.8 "Cấp quyền": the matrix of rights × permission levels; change a level, add one, reset a system level. */
export default function AccessLevelsPage() {
  const { t } = useTranslation();
  const can = useCan();
  const editable = can("role.edit");
  const rights = useQuery({
    queryKey: ["access-rights"],
    queryFn: listAccessRights,
  });
  const levels = useQuery({
    queryKey: ["permission-levels"],
    queryFn: listPermissionLevels,
  });
  const [editing, setEditing] = useState<PermissionLevel | true | null>(null);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [picked, setPicked] = useState<string[]>([]);
  const invalidate = [["permission-levels"]];
  const save = useApiMutation(
    () =>
      savePermissionLevel(editing === true ? null : editing!.id, {
        name,
        description,
        rights: picked,
      }),
    {
      invalidate,
      successMessage: t("accessLevels.saved"),
      onSuccess: () => setEditing(null),
    },
  );
  const remove = useApiMutation(deletePermissionLevel, {
    invalidate,
    successMessage: t("accessLevels.deleted"),
  });
  const reset = useApiMutation(resetPermissionLevel, {
    invalidate,
    successMessage: t("accessLevels.resetDone"),
  });
  const open = (l: PermissionLevel | true) => {
    setEditing(l);
    setName(l === true ? "" : l.name);
    setDescription(l === true ? "" : l.description);
    setPicked(l === true ? ["ViewItems", "OpenItems"] : (l.rightNames ?? []));
  };
  const list = levels.data ?? [];
  return (
    <>
      <PageHeader
        title={t("menu.accessLevels")}
        description={t("accessLevels.description")}
        extra={
          editable && (
            <Button
              type="primary"
              icon={<PlusOutlined />}
              onClick={() => open(true)}
            >
              {t("accessLevels.new")}
            </Button>
          )
        }
      />
      <Alert
        type="info"
        showIcon
        message={t("accessLevels.hint")}
        style={{ marginBottom: 12 }}
      />
      <Table<AccessRightInfo>
        rowKey="name"
        size="small"
        loading={rights.isLoading || levels.isLoading}
        dataSource={rights.data ?? []}
        pagination={false}
        scroll={{ x: "max-content" }}
        columns={[
          {
            title: t("accessLevels.right"),
            key: "right",
            fixed: "left",
            render: (_, r) => (
              <Space direction="vertical" size={0}>
                <span>{r.description}</span>
                <Tag style={{ fontSize: 11 }}>{r.group}</Tag>
              </Space>
            ),
          },
          ...list.map((l) => ({
            title: (
              <Space
                direction="vertical"
                size={2}
                style={{ textAlign: "center" }}
              >
                <strong>{l.name}</strong>
                {l.isCustomized && (
                  <Tag color="orange">{t("accessLevels.customized")}</Tag>
                )}
                {!l.isSystem && (
                  <Tag color="blue">{t("accessLevels.custom")}</Tag>
                )}
                {editable && l.code !== "owner" && (
                  <Space size={0}>
                    <Tooltip title={t("common.edit")}>
                      <Button
                        size="small"
                        type="text"
                        icon={<EditOutlined />}
                        onClick={() => open(l)}
                        aria-label={t("common.edit")}
                      />
                    </Tooltip>
                    {l.isSystem && l.isCustomized && (
                      <Popconfirm
                        title={t("accessLevels.resetConfirm", { name: l.name })}
                        onConfirm={() => reset.mutate(l.id)}
                      >
                        <Button
                          size="small"
                          type="text"
                          icon={<UndoOutlined />}
                          aria-label={t("accessLevels.reset")}
                        />
                      </Popconfirm>
                    )}
                    {!l.isSystem && (
                      <Popconfirm
                        title={t("accessLevels.deleteConfirm", {
                          name: l.name,
                        })}
                        onConfirm={() => remove.mutate(l.id)}
                      >
                        <Button
                          size="small"
                          type="text"
                          danger
                          icon={<DeleteOutlined />}
                          aria-label={t("common.delete")}
                        />
                      </Popconfirm>
                    )}
                  </Space>
                )}
              </Space>
            ),
            key: l.id,
            align: "center" as const,
            render: (_: unknown, r: AccessRightInfo) => (
              <Checkbox
                checked={(l.rightNames ?? []).includes(r.name)}
                disabled
                aria-label={`${l.name}: ${r.name}`}
              />
            ),
          })),
        ]}
      />
      {editing && (
        <Modal
          open
          width={640}
          title={
            editing === true
              ? t("accessLevels.new")
              : t("accessLevels.edit", { name: editing.name })
          }
          okText={t("common.save")}
          okButtonProps={{ disabled: !name.trim(), loading: save.isPending }}
          onOk={() => save.mutate(undefined)}
          onCancel={() => setEditing(null)}
          destroyOnHidden
        >
          <Space direction="vertical" style={{ width: "100%" }}>
            <Input
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder={t("accessLevels.name")}
              aria-label={t("accessLevels.name")}
              maxLength={255}
            />
            <Input.TextArea
              rows={2}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder={t("accessLevels.descriptionLabel")}
              maxLength={1000}
            />
            <Checkbox.Group
              style={{ display: "grid", gap: 6 }}
              value={picked}
              onChange={(v) => setPicked(v as string[])}
              options={(rights.data ?? []).map((r) => ({
                value: r.name,
                label: `${r.description} (${r.group})`,
                disabled: r.name === "ViewItems",
              }))}
            />
          </Space>
        </Modal>
      )}
    </>
  );
}
