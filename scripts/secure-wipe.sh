#!/usr/bin/env sh
# Xóa sạch dữ liệu khi kết thúc hợp đồng và lập biên bản (xem secure-wipe.ps1 để biết chi tiết).
set -eu
cd "$(dirname "$0")/../deploy"
printf "CẢNH BÁO: xóa vĩnh viễn toàn bộ dữ liệu. Gõ 'XOA TOAN BO DU LIEU': "; read -r ans
[ "$ans" = "XOA TOAN BO DU LIEU" ] || { echo "Đã hủy"; exit 1; }
printf "Người thực hiện: "; read -r op
printf "Đại diện Chủ đầu tư: "; read -r wit
R="bien-ban-xoa-du-lieu-$(date +%Y%m%d-%H%M%S).txt"
{
  echo "BIÊN BẢN XÓA DỮ LIỆU HỆ THỐNG BLUEDRIVE"
  echo "Thời điểm bắt đầu: $(date -R)"; echo "Máy chủ: $(hostname)"; echo "Người thực hiện: $op"; echo "Đại diện Chủ đầu tư: $wit"; echo
  echo "DANH SÁCH VOLUME TRƯỚC KHI XÓA:"
  for v in $(docker volume ls --filter label=com.docker.compose.project=bluedrive --format '{{.Name}}'); do
    info=$(docker run --rm -v "$v:/v:ro" alpine:3.20 sh -c 'echo "$(du -sb /v | cut -f1) byte, $(find /v -type f | wc -l) tệp, mã băm $(find /v -type f -exec ls -l {} + 2>/dev/null | sha256sum | cut -d" " -f1)"')
    echo "- $v : $info"
  done
} > "$R"
docker compose --profile clamav down --volumes --remove-orphans
if [ -f .env ]; then shred -u .env 2>/dev/null || rm -f .env; echo "Đã hủy deploy/.env (khóa chủ mã hóa)." >> "$R"; fi
left=$(docker volume ls --filter label=com.docker.compose.project=bluedrive --format '{{.Name}}')
{ echo; echo "Volume còn lại: ${left:-không còn}"; echo "Thời điểm kết thúc: $(date -R)"; } >> "$R"
echo "SHA-256 của biên bản (trước dòng này): $(sha256sum "$R" | cut -d' ' -f1)" >> "$R"
echo "Đã lập biên bản: deploy/$R"
