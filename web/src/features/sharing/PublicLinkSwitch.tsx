import { Divider, Space, Switch, Typography } from "antd";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import {
  getItemSharingPolicy,
  listPermissionLevels,
  setItemExternalSharing,
} from "@/api/permissions";
import { qk } from "@/api/queryKeys";
import {
  createShareLink,
  listShareLinks,
  updateShareLink,
} from "@/api/sharing";
import { useApiMutation } from "@/hooks/useApiMutation";
import type { ResourceType, SaveShareLinkRequest } from "@/types/sharing";

/**
 * #49 "Chia sẻ ra ngoài": one switch. On, the item's link (tab "Chia sẻ qua liên kết") opens for anyone who has it, without signing in,
 * as a viewer; off, only for people who already have access. Turned off by the system, the Space, a folder above or a label, it says
 * which and cannot be turned on here.
 */
export default function PublicLinkSwitch({
  resourceType,
  resourceId,
}: {
  resourceType: ResourceType;
  resourceId: string;
}) {
  const { t } = useTranslation();
  const linksKey = qk.shareLinks(resourceType, resourceId);
  const links = useQuery({
    queryKey: linksKey,
    queryFn: () => listShareLinks(resourceType, resourceId),
  });
  const policy = useQuery({
    queryKey: ["item-sharing-policy", resourceId],
    queryFn: () => getItemSharingPolicy(resourceId),
  });
  const levels = useQuery({
    queryKey: ["permission-levels"],
    queryFn: listPermissionLevels,
    staleTime: 5 * 60_000,
  });
  const viewerId = levels.data?.find((l) => l.code === "viewer")?.id ?? null;
  const active = [...(links.data ?? [])]
    .filter((l) => l.status === "active")
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  const current = active.find((l) => !!l.url) ?? active[0];
  const on = current?.scope === "anyone";
  const p = policy.data;
  // The item's own setting does not count as a block: turning the switch on lifts it.
  const blocked =
    !!p &&
    !p.anyoneLinksAllowed &&
    !(p.itemExternal !== null && p.externalSource === "Mục này");

  const toggle = useApiMutation(
    async (wanted: boolean) => {
      if (wanted && p?.itemExternal && p.itemExternal !== "anyone")
        await setItemExternalSharing(resourceId, null);
      const body: SaveShareLinkRequest = {
        resourceType,
        resourceId,
        allowDownload: current?.allowDownload ?? true,
        allowEdit: false,
        requireLogin: !wanted,
        scope: wanted ? "anyone" : "existing",
        levelId: wanted ? viewerId : null,
        people: [],
        expiresAt: wanted
          ? current?.scope === "anyone"
            ? current.expiresAt
            : null
          : null,
        maxAccessCount: wanted ? (current?.maxAccessCount ?? null) : null,
      };
      if (current?.url) await updateShareLink(current.id, body);
      else await createShareLink(body);
    },
    {
      invalidate: [linksKey, ["item-sharing-policy"]],
      successMessage: (_, wanted) =>
        wanted ? t("sharing.publicOn") : t("sharing.publicOff"),
    },
  );

  if (!p || links.isLoading) return null;
  return (
    <>
      <Divider orientation="left" orientationMargin={0}>
        {t("sharing.people.external")}
      </Divider>
      <Space align="start">
        <Switch
          checked={on}
          loading={toggle.isPending}
          disabled={(blocked && !on) || !p.canChangeItem}
          onChange={(v) => toggle.mutate(v)}
          aria-label={t("sharing.publicSwitch")}
        />
        <div>
          <Typography.Text strong>{t("sharing.publicSwitch")}</Typography.Text>
          <Typography.Paragraph
            type="secondary"
            style={{ fontSize: 12, marginBottom: 0 }}
          >
            {blocked && !on
              ? t("sharing.publicBlocked", { source: p.externalSource })
              : on
                ? t("sharing.publicOnHint")
                : t("sharing.publicOffHint")}
          </Typography.Paragraph>
        </div>
      </Space>
    </>
  );
}
