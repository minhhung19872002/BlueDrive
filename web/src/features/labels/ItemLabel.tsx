import { useState } from "react";
import { Button, Input, Modal, Select, Space, Tag, Typography } from "antd";
import { TagOutlined } from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { getItemLabel, listLabels, setItemLabel } from "@/api/labels";
import { useApiMutation } from "@/hooks/useApiMutation";

/** Spec 7.1: the label of a file, folder or Space (where it comes from), changed by who may; lowering asks for a reason. */
export default function ItemLabel({ itemId }: { itemId: string }) {
  const { t } = useTranslation();
  const key = ["item-label", itemId];
  const info = useQuery({ queryKey: key, queryFn: () => getItemLabel(itemId) });
  const labels = useQuery({
    queryKey: ["labels"],
    queryFn: listLabels,
    staleTime: 10 * 60_000,
  });
  const [editing, setEditing] = useState(false);
  const [picked, setPicked] = useState<string | null>(null);
  const [reason, setReason] = useState("");
  const save = useApiMutation(
    () => setItemLabel(itemId, picked, reason.trim() || null),
    {
      invalidate: [key, ["drive"], ["item-sharing-policy"]],
      successMessage: t("labels.changed"),
      onSuccess: () => setEditing(false),
    },
  );
  if (!info.data) return null;
  const current = info.data.label;
  const next = (labels.data ?? []).find((l) => l.id === picked);
  const lowering = !!current && (next?.priority ?? -1) < current.priority;
  return (
    <>
      <Space wrap size={4}>
        {current ? (
          <Tag color={current.color} icon={<TagOutlined />}>
            {current.name}
          </Tag>
        ) : (
          <Typography.Text type="secondary">{t("labels.none")}</Typography.Text>
        )}
        {current && info.data.source && (
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            ({info.data.source})
          </Typography.Text>
        )}
        {info.data.canChange && (
          <Button
            size="small"
            type="link"
            style={{ padding: 0 }}
            onClick={() => {
              setPicked(info.data!.own?.id ?? null);
              setReason("");
              setEditing(true);
            }}
          >
            {t("labels.change")}
          </Button>
        )}
      </Space>
      {editing && (
        <Modal
          open
          title={t("labels.changeTitle")}
          okText={t("common.save")}
          okButtonProps={{
            loading: save.isPending,
            disabled: lowering && !reason.trim(),
          }}
          onOk={() => save.mutate(undefined)}
          onCancel={() => setEditing(false)}
          destroyOnHidden
        >
          <Space direction="vertical" style={{ width: "100%" }}>
            <Select
              allowClear
              style={{ width: "100%" }}
              value={picked ?? undefined}
              placeholder={t("labels.inherit")}
              onChange={(v) => setPicked(v ?? null)}
              options={(labels.data ?? [])
                .filter((l) => l.isActive)
                .map((l) => ({
                  value: l.id,
                  label: <Tag color={l.color}>{l.name}</Tag>,
                  title: l.description,
                }))}
              aria-label={t("labels.label")}
            />
            {next && (
              <Typography.Paragraph type="secondary" style={{ fontSize: 12 }}>
                {next.description}
              </Typography.Paragraph>
            )}
            {lowering && (
              <Input.TextArea
                rows={2}
                maxLength={500}
                value={reason}
                onChange={(e) => setReason(e.target.value)}
                placeholder={t("labels.reason")}
                aria-label={t("labels.reason")}
              />
            )}
          </Space>
        </Modal>
      )}
    </>
  );
}
