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
# Object storage: empty the bucket (blobs + backups/) while the stack is still configured.
s3_left=0
bucket=""
[ -f .env ] && bucket=$(grep -E '^[[:space:]]*S3_BUCKET[[:space:]]*=' .env | tail -n1 | sed -e 's/^[^=]*=[[:space:]]*//' -e 's/[[:space:]]*$//' | tr -d '\r') || true
if [ -n "$bucket" ]; then
  out=$(docker compose run --rm --no-deps -T worker wipe-s3 || true)
  val() { printf '%s\n' "$out" | sed -n "s/^$1=//p" | tail -n1 | tr -d '\r'; }
  if [ "$(val S3_CONFIGURED)" = "true" ]; then
    s3_left=$(val S3_REMAINING)
    { echo; echo "LƯU TRỮ S3 (bucket $(val S3_BUCKET)):"
      echo "- Tệp số hóa đã xóa: $(val S3_DATA_OBJECTS) đối tượng, $(val S3_DATA_BYTES) byte"
      echo "- Bản sao lưu (backups/) đã xóa: $(val S3_BACKUP_OBJECTS) đối tượng, $(val S3_BACKUP_BYTES) byte"
      echo "- Đối tượng còn lại trong bucket: $s3_left"
      echo "- Lưu ý: nếu bucket bật versioning / object lock, các phiên bản cũ phải được xóa thêm trên giao diện nhà cung cấp S3."; } >> "$R"
  else
    s3_left=1
    { echo; echo "LƯU TRỮ S3: không xóa được bucket $bucket - CẦN XỬ LÝ THỦ CÔNG."; } >> "$R"
  fi
fi
docker compose --profile clamav down --volumes --remove-orphans
if [ -f .env ]; then shred -u .env 2>/dev/null || rm -f .env; echo "Đã hủy deploy/.env (khóa chủ mã hóa)." >> "$R"; fi
left=$(docker volume ls --filter label=com.docker.compose.project=bluedrive --format '{{.Name}}')
if [ -n "$left" ] || [ "${s3_left:-0}" != "0" ]; then result="CHƯA HOÀN TẤT"; else result="ĐÃ XÓA TOÀN BỘ DỮ LIỆU"; fi
{ echo; echo "Volume còn lại: ${left:-không còn}"; echo "Thời điểm kết thúc: $(date -R)"; echo "Kết quả: $result"; } >> "$R"
echo "SHA-256 của biên bản (trước dòng này): $(sha256sum "$R" | cut -d' ' -f1)" >> "$R"
echo "Đã lập biên bản: deploy/$R"
