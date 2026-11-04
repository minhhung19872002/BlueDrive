"""Per-minute view of a k6 CSV result (tests/load/results/<name>.csv.gz): VUs, requests/s, p95 / max latency and errors,
plus the slowest endpoints of the whole run. Used for docs/bao-cao-kiem-thu-tai.md (where does p95 cross 2 s in T2?).
    py tests/load/analyze.py tests/load/results/t2.csv.gz [--kind read]
"""
import argparse
import csv
import gzip
from urllib.parse import parse_qs
from collections import defaultdict


def p(values, q):
    if not values:
        return 0.0
    values.sort()
    return values[min(len(values) - 1, int(q * len(values)))]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('file')
    ap.add_argument('--kind', help='only requests with this kind tag (read, write, auth, upload)')
    args = ap.parse_args()

    minutes = defaultdict(lambda: {'dur': [], 'failed': 0, 'vus': 0})
    endpoints = defaultdict(list)
    endpoint_failed = defaultdict(int)
    start = None
    with gzip.open(args.file, 'rt', encoding='utf-8', newline='') as fh:
        for row in csv.DictReader(fh):
            t = int(row['timestamp'])
            start = t if start is None else min(start, t)
            m = row['metric_name']
            if m == 'vus':
                minutes[t // 60]['vus'] = max(minutes[t // 60]['vus'], int(float(row['metric_value'])))
                continue
            # Custom tags (kind) are not CSV columns: k6 writes them to extra_tags as a query string.
            kind = parse_qs(row.get('extra_tags') or '').get('kind', [None])[0]
            if args.kind and kind != args.kind:
                continue
            if m == 'http_req_duration':
                minutes[t // 60]['dur'].append(float(row['metric_value']))
                endpoints[row.get('name', '?')].append(float(row['metric_value']))
            elif m == 'http_req_failed' and row['metric_value'] in ('1', '1.000000'):
                minutes[t // 60]['failed'] += 1
                endpoint_failed[row.get('name', '?')] += 1

    print(f"{'phút':>4} {'VU':>4} {'req/s':>7} {'p95 ms':>8} {'max ms':>8} {'lỗi':>6}")
    first = min(minutes)
    for k in sorted(minutes):
        d = minutes[k]
        n = len(d['dur'])
        print(f"{k - first:>4} {d['vus']:>4} {n / 60:>7.1f} {p(d['dur'], 0.95):>8.0f} {max(d['dur'], default=0):>8.0f} {d['failed']:>6}")

    print('\nĐiểm cuối chậm nhất (p95 toàn bài):')
    rows = sorted(((p(v, 0.95), len(v), max(v), name) for name, v in endpoints.items()), reverse=True)
    for p95, n, mx, name in rows[:15]:
        print(f"  {name:<45} n={n:>7}  p95 {p95:>7.0f} ms  max {mx:>7.0f} ms  lỗi {endpoint_failed[name]}")


if __name__ == '__main__':
    main()
