#!/usr/bin/env bash
set -euo pipefail

root=${1:-$(cd "$(dirname "$0")/.." && pwd)}
worker="$root/worker/nas-worker.sh"
tmp=$(mktemp -d)
trap 'status=$?; if [ "$status" -ne 0 ]; then echo "Test artifacts: $tmp" >&2; else rm -rf "$tmp"; fi' EXIT

mkdir -p "$tmp/home/bin" "$tmp/home/.cache/imgzip/jobs"
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
: > "$out/$(basename -- "$src")"
EOF
chmod +x "$tmp/home/bin/caesiumclt"

run_worker() {
  local source=$1 recurse=$2 copy=$3 dry=$4 id=$5
  local job="$tmp/home/.cache/imgzip/jobs/$id"
  mkdir -p "$job"
  local source_b64
  source_b64=$(printf '%s' "$source" | base64 | tr -d '\r\n')
  HOME="$tmp/home" MODE=execute JOB_ID="$id" SOURCES_B64="$source_b64" FORMAT=keep QUALITY=90 \
    RESIZE=none PIXELS=0 MAX_SIZE=0 THREADS=1 LOSSLESS=false NO_UPSCALE=true RECURSE="$recurse" \
    COPY_SKIPPED="$copy" DRY_RUN="$dry" bash "$worker" >"$job/out.events" 2>"$job/err.log"
}

basic="$tmp/basic"
mkdir -p "$basic/sub"
printf 'image' > "$basic/one.jpg"
printf 'note' > "$basic/note.txt"
printf 'cache' > "$basic/Thumbs.db"
printf 'hidden' > "$basic/.hidden.txt"
printf 'nested' > "$basic/sub/nested.txt"
symlink_created=false
if ln -s note.txt "$basic/link.txt" 2>/dev/null && [ -L "$basic/link.txt" ]; then symlink_created=true; fi
touch -t 202001020304.05 "$basic/note.txt"
before_mtime=$(stat -c %Y "$basic/note.txt")
run_worker "$basic" false true false 00000000000000000000000000000011
output="${basic}_compressed"
[ -f "$output/one.jpg" ] || { echo 'FAIL missing compressed image' >&2; exit 1; }
[ -f "$output/note.txt" ] || { echo 'FAIL missing copied note' >&2; exit 1; }
[ -f "$output/Thumbs.db" ] && [ -f "$output/.hidden.txt" ] || { echo 'FAIL ordinary hidden files missing' >&2; exit 1; }
[ ! -e "$output/sub/nested.txt" ] || { echo 'FAIL non-recursive copy entered subdirectory' >&2; exit 1; }
[ "$symlink_created" != true ] || [ ! -e "$output/link.txt" ] || { echo 'FAIL symlink was copied' >&2; exit 1; }
after_mtime=$(stat -c %Y "$output/note.txt")
[ "$after_mtime" = "$before_mtime" ] || { echo 'FAIL copied mtime changed' >&2; exit 1; }
grep -q '"type":"copy"' "$tmp/home/.cache/imgzip/jobs/00000000000000000000000000000011/out.events"

recursive="$tmp/recursive"
mkdir -p "$recursive/sub"
printf 'image' > "$recursive/one.jpg"
printf 'nested' > "$recursive/sub/nested.txt"
run_worker "$recursive" true true false 00000000000000000000000000000012
[ -f "${recursive}_compressed/sub/nested.txt" ] || { echo 'FAIL recursive copied file missing' >&2; exit 1; }

dry_source="$tmp/dry"
mkdir -p "$dry_source"
printf 'image' > "$dry_source/one.jpg"
printf 'sidecar' > "$dry_source/sidecar.raw"
run_worker "$dry_source" false true true 00000000000000000000000000000013
[ ! -e "${dry_source}_compressed" ] || { echo 'FAIL dry run created output' >&2; exit 1; }
grep -q '"copyTotal":1' "$tmp/home/.cache/imgzip/jobs/00000000000000000000000000000013/out.events"

copy_only="$tmp/copy-only"
mkdir -p "$copy_only"
printf 'video' > "$copy_only/video.mov"
run_worker "$copy_only" false true false 00000000000000000000000000000014
[ -f "${copy_only}_compressed/video.mov" ] || { echo 'FAIL copy-only output missing' >&2; exit 1; }
grep -q '"state":"succeeded"' "$tmp/home/.cache/imgzip/jobs/00000000000000000000000000000014/out.events"

echo 'PASS nas-worker copy other files'
