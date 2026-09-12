#!/usr/bin/env bash
set -euo pipefail

root=${1:-$(cd "$(dirname "$0")/.." && pwd)}
worker="$root/worker/nas-worker.sh"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

mkdir -p "$tmp/home/bin" "$tmp/home/.cache/imgzip/jobs" "$tmp/source"
cat > "$tmp/home/bin/caesiumclt" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
out=''
src=''
while [ "$#" -gt 0 ]; do
  case "$1" in
    -o) out=$2; shift 2;;
    *) src=$1; shift;;
  esac
done
[ -n "$out" ] && [ -n "$src" ]
sleep 0.02
: > "$out/$(basename -- "$src")"
EOF
chmod +x "$tmp/home/bin/caesiumclt"

for index in $(seq 1 4); do : > "$tmp/source/$index.jpg"; done
source_b64=$(printf '%s' "$tmp/source" | base64 | tr -d '\r\n')
nproc_value=$(nproc 2>/dev/null || printf 1)
[ "$nproc_value" -ge 1 ] || nproc_value=1

max_dispatched() {
  awk '/^dispatch:/ { for (i=1; i<=NF; i++) if ($i ~ /^running=/) { split($i, value, "="); current=value[2]+1; if (current>max) max=current } } END { print max+0 }' "$1"
}

run_case() {
  local name=$1 requested=$2 memory=$3 expected=$4 job_id=$5
  local job="$tmp/home/.cache/imgzip/jobs/$job_id"
  mkdir -p "$job"
  HOME="$tmp/home" MODE=execute JOB_ID="$job_id" SOURCES_B64="$source_b64" FORMAT=keep QUALITY=90 \
    RESIZE=none PIXELS=0 MAX_SIZE=0 THREADS="$requested" LOSSLESS=false NO_UPSCALE=true RECURSE=false DRY_RUN=false \
    IMGZIP_MEM_AVAILABLE_KB="$memory" bash "$worker" >"$tmp/$name.events" 2>"$tmp/$name.stderr"
  local actual
  actual=$(max_dispatched "$job/diagnostic.log")
  if [ "$actual" -ne "$expected" ]; then
    printf 'FAIL %s: expected max concurrency %s, got %s\n' "$name" "$expected" "$actual" >&2
    cat "$job/diagnostic.log" >&2
    exit 1
  fi
  printf 'PASS nas-worker %s: max concurrency=%s\n' "$name" "$actual"
}

hardware_cap=$(( nproc_value < 4 ? nproc_value : 4 ))
run_case "cap-32" 32 4000000 "$hardware_cap" 00000000000000000000000000000001
run_case "fallback-zero" 0 4000000 "$(( hardware_cap < 2 ? hardware_cap : 2 ))" 00000000000000000000000000000002
run_case "fallback-invalid" abc 4000000 "$(( hardware_cap < 2 ? hardware_cap : 2 ))" 00000000000000000000000000000003
run_case "memory-guard" 4 1000000 1 00000000000000000000000000000004
