import { useEffect, useState } from "react";
import { useAuthStore } from "@/stores/auth";

/**
 * Spec 11.9: a dynamic watermark over what is viewed "Chỉ xem" or under a label requiring it — the viewer's name, account and the time,
 * repeated across the content (it does not stop a photo of the screen, it makes it traceable). The context menu is turned off as well.
 */
export default function Watermark() {
  const user = useAuthStore((s) => s.user);
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const id = window.setInterval(() => setNow(new Date()), 60_000);
    return () => window.clearInterval(id);
  }, []);
  const text = `${user?.fullName ?? ""} · ${user?.email ?? user?.username ?? ""} · ${now.toLocaleString("vi-VN")}`;
  const tile = `<svg xmlns='http://www.w3.org/2000/svg' width='420' height='220'><text x='10' y='120' transform='rotate(-22 210 110)' font-family='sans-serif' font-size='15' fill='rgba(120,120,120,0.28)'>${text
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")}</text></svg>`;
  return (
    <div
      aria-hidden
      className="bd-watermark"
      onContextMenu={(e) => e.preventDefault()}
      style={{
        position: "absolute",
        inset: 0,
        pointerEvents: "none",
        zIndex: 5,
        backgroundImage: `url("data:image/svg+xml;utf8,${encodeURIComponent(tile)}")`,
        backgroundRepeat: "repeat",
      }}
    />
  );
}
