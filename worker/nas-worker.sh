#!/usr/bin/env bash
# Received via SSH stdin. Dynamic values are base64-encoded in the request prelude.
# No jq/Python dependency. Requires Bash 4+, GNU find/stat/coreutils, util-linux setsid.
set -uo pipefail
umask 077
encode() { printf '%s' "$1" | base64 | tr -d '\r\n'; }
decode() { printf '%s' "$1" | base64 -d; }
job_root="$HOME/.cache/imgzip/jobs"
job_dir="$job_root/$JOB_ID"
engine="$HOME/bin/caesiumclt"
total=0 completed=0 succeeded=0 failed=0 skipped=0 before=0 after=0
output='' stage='' dry=false
event() {
  local kind="$1" state="$2" path="${3:-}" message="${4:-}" index="${5:--1}" size_before="${6:-$before}" size_after="${7:-$after}"
  printf '{"version":1,"jobId":"%s","type":"%s","state":"%s","total":%s,"completed":%s,"succeeded":%s,"failed":%s,"skipped":%s,"beforeBytes":%s,"afterBytes":%s,"index":%s,"dryRun":%s,"path64":"%s","output64":"%s","message64":"%s"}\n' \
    "$JOB_ID" "$kind" "$state" "$total" "$completed" "$succeeded" "$failed" "$skipped" "$size_before" "$size_after" "$index" "$dry" "$(encode "$path")" "$(encode "$output")" "$(encode "$message")"
}
terminal() {
  # stdout is the journal during execution; finish is visible only after its event is durable.
  event finished "$1" '' "${2:-}"
  if [ "$MODE" = execute ]; then : > "$job_dir/done"; fi
}
cleanup_stage() {
  if [ -n "$stage" ] && [[ "$stage" == "$output"/.imgzip-* ]]; then rm -rf -- "$stage"; fi
}
group_has_children() {
  local list pid group
  list=$(ps -eo pid=,pgid=) || return 0
  while read -r pid group; do
    # The snapshot's short-lived ps process has exited before this liveness check.
    if [ "$group" = "$$" ] && [ "$pid" != "$$" ] && kill -0 "$pid" 2>/dev/null; then return 0; fi
  done <<< "$list"
  return 1
}
cancelled() {
  trap '' TERM INT
  # A child may ignore TERM. Do not acknowledge until the task group has drained.
  for attempt in 1 2 3 4 5 6 7 8 9 10; do
    if ! group_has_children; then cleanup_stage; terminal cancelled 'NAS task cancellation confirmed'; exit 0; fi
    sleep 0.5
  done
  printf '%s\n' 'Task descendants have not confirmed termination' >&2
  exit 0
}
unknown() { terminal unknown "$1"; exit 0; }
case_match() {
  local parent leaf entry lower
  parent=$(dirname -- "$1"); leaf=$(basename -- "$1" | tr '[:upper:]' '[:lower:]')
  [ -d "$parent" ] || return 1
  while IFS= read -r -d '' entry; do
    lower=$(basename -- "$entry" | tr '[:upper:]' '[:lower:]')
    if [ "$lower" = "$leaf" ]; then printf '%s' "$entry"; return 0; fi
  done < <(find "$parent" -mindepth 1 -maxdepth 1 -print0)
  return 1
}

case "$MODE" in
  probe)
    ok=true
    for utility in bash find stat base64 setsid ps awk; do command -v "$utility" >/dev/null 2>&1 || ok=false; done
    [ -x "$engine" ] || ok=false
    [ "${BASH_VERSINFO[0]}" -ge 4 ] || ok=false
    "$engine" --version >/dev/null 2>&1 || ok=false
    printf '{"version":1,"jobId":"%s","type":"probe","connected":true,"nasAvailable":%s,"message64":"%s"}\n' "$JOB_ID" "$ok" "$(encode 'SSH connected; engine and required utilities checked')"
    exit 0;;
  resolve)
    share=$(decode "$SHARE_B64")
    share_path=$(awk -v sec="[$share]" 'tolower($0)==tolower(sec){f=1;next} f&&/^\[/{exit} f&&tolower($1)=="path"{sub(/^[^=]*=[ ]*/,"");print;exit}' /etc/samba/users/*.share.conf /etc/samba/smb.conf /etc/samba/smb.custom.conf 2>/dev/null || true)
    if [ -z "$share_path" ]; then share_path=$(testparm -s --parameter-name=path --section-name="$share" 2>/dev/null | head -n 1 || true); fi
    if [ -z "$share_path" ] || [[ "$share_path" != /* ]]; then event error failed '' 'Cannot resolve Samba share path'; exit 0; fi
    event resolved ready "$share_path"; exit 0;;
  inspect)
    if [ ! -d "$job_dir" ]; then unknown 'Remote job record not found'; fi
    [ ! -f "$job_dir/events.jsonl" ] || cat "$job_dir/events.jsonl"
    if [ ! -f "$job_dir/done" ]; then unknown 'Remote job has not confirmed completion; check again'; fi
    exit 0;;
  cancel)
    for attempt in 1 2 3 4 5 6 7 8 9 10; do [ -d "$job_dir" ] && break; sleep 0.5; done
    if [ ! -d "$job_dir" ]; then unknown 'Remote job record not found; cannot confirm cancellation'; fi
    if [ -f "$job_dir/done" ]; then cat "$job_dir/events.jsonl"; exit 0; fi
    : > "$job_dir/cancel"
    if [ -f "$job_dir/process" ]; then
      read -r task_pid task_start < "$job_dir/process"
      current_start=$(awk '{print $22}' "/proc/$task_pid/stat" 2>/dev/null || true)
      session_id=$(ps -o sid= -p "$task_pid" 2>/dev/null | tr -d ' ')
      if [[ "$task_pid" =~ ^[0-9]+$ ]] && [ "$current_start" = "$task_start" ] && [ "$session_id" = "$task_pid" ]; then
        kill -TERM -- "-$task_pid" 2>/dev/null || true
      fi
    fi
    for attempt in 1 2 3 4 5 6 7 8 9 10; do
      if [ -f "$job_dir/done" ]; then cat "$job_dir/events.jsonl"; exit 0; fi
      sleep 0.5
    done
    unknown 'Cancellation requested but remote termination is not yet confirmed';;
  launch)
    command -v setsid >/dev/null 2>&1 || { terminal failed 'setsid is required for verifiable NAS cancellation'; exit 0; }
    mkdir -p -- "$job_root" || exit 1
    mkdir -- "$job_dir" || { terminal failed 'Task ID already exists'; exit 0; }
    for variable in JOB_ID SOURCES_B64 FORMAT QUALITY RESIZE PIXELS MAX_SIZE THREADS LOSSLESS NO_UPSCALE RECURSE DRY_RUN; do declare -p "$variable" >> "$job_dir/request.sh"; done
    printf '%s' "$SCRIPT_B64" | base64 -d > "$job_dir/runner.sh" || exit 1
    : > "$job_dir/events.jsonl"
    # Each task has its own session/process group. The SSH connection is just a reader.
    setsid bash -c 'source "$1/request.sh"; MODE=execute; export MODE; source "$1/runner.sh"' imgzip "$job_dir" \
      </dev/null >>"$job_dir/events.jsonl" 2>"$job_dir/diagnostic.log" &
    next_line=1; startup_ticks=0
    while :; do
      count=$(wc -l < "$job_dir/events.jsonl")
      if [ "$count" -ge "$next_line" ]; then sed -n "${next_line},${count}p" "$job_dir/events.jsonl"; next_line=$((count + 1)); fi
      if [ -f "$job_dir/done" ]; then
        sed -n "${next_line},\$p" "$job_dir/events.jsonl"
        exit 0
      fi
      # Detect an early crash before a terminal event rather than hanging indefinitely.
      if [ -f "$job_dir/process" ]; then
        read -r task_pid task_start < "$job_dir/process"
        current_start=$(awk '{print $22}' "/proc/$task_pid/stat" 2>/dev/null || true)
        if [ "$current_start" != "$task_start" ] && [ ! -f "$job_dir/done" ]; then unknown 'Remote runner exited without a terminal event'; fi
      else
        startup_ticks=$((startup_ticks + 1))
        if [ "$startup_ticks" -ge 34 ]; then unknown 'Remote runner did not acknowledge startup'; fi
      fi
      sleep 0.3
    done;;
  execute) ;;
  *) event error failed '' 'Unknown remote operation'; exit 1;;
esac

trap cancelled TERM INT
printf '%s %s\n' "$$" "$(awk '{print $22}' /proc/$$/stat)" > "$job_dir/process"
[ "$DRY_RUN" != true ] || dry=true
event phase preparing '' 'Building NAS file manifest'
if [ ! -x "$engine" ] && [ "$dry" != true ]; then terminal failed 'caesiumclt is missing; run the existing -Setup command'; exit 0; fi
sources=()
while IFS= read -r line; do [ -z "$line" ] || sources+=("$(decode "$line")"); done <<< "$SOURCES_B64"
if [ "${#sources[@]}" -eq 0 ]; then terminal failed 'No input paths'; exit 0; fi
source_root="${sources[0]%/}"
if [ ! -d "$source_root" ]; then source_root=$(dirname -- "$source_root"); fi
if [ "${#sources[@]}" -gt 1 ]; then
  for source_path in "${sources[@]}"; do
    if [ ! -f "$source_path" ] || [ "$(dirname -- "$source_path")" != "$source_root" ]; then terminal failed 'Select images from one directory'; exit 0; fi
  done
fi
files=()
include_file() {
  local file="$1" lower
  lower=$(printf '%s' "$file" | tr '[:upper:]' '[:lower:]')
  if [ -L "$file" ]; then skipped=$((skipped + 1)); return; fi
  case "$lower" in *.jpg|*.jpeg|*.png|*.webp|*.gif) files+=("$file");; *) skipped=$((skipped + 1));; esac
}
for source_path in "${sources[@]}"; do
  if [ -L "$source_path" ]; then skipped=$((skipped + 1)); continue; fi
  if [ -d "$source_path" ]; then
    depth=(); [ "$RECURSE" = true ] || depth=(-maxdepth 1)
    # Use a materialized NUL-delimited manifest so enumeration failure cannot look like success.
    if ! find -P "$source_path" "${depth[@]}" \( -type f -o -type l \) -print0 > "$job_dir/scan"; then
      terminal failed 'Cannot enumerate source directory'; exit 0
    fi
    while IFS= read -r -d '' file; do include_file "$file"; done < "$job_dir/scan"
  elif [ -f "$source_path" ]; then include_file "$source_path"
  else terminal failed 'Source path does not exist'; exit 0; fi
done
total=${#files[@]}
planned=()
declare -A used_names=()
for file in "${files[@]}"; do
  relative="${file#"$source_root"/}"; directory=$(dirname -- "$relative")
  leaf=$(basename -- "$relative"); stem="${leaf%.*}"; extension=$(printf '%s' "${leaf##*.}" | tr '[:upper:]' '[:lower:]')
  if [ "$FORMAT" != keep ]; then extension="$FORMAT"; [ "$extension" != jpeg ] || extension=jpg; fi
  leaf="$stem.$extension"; serial=2
  while :; do
    candidate="$directory/$leaf"; key=$(printf '%s' "$candidate" | tr '[:upper:]' '[:lower:]')
    [ -n "${used_names[$key]:-}" ] || break
    leaf="${stem}_$serial.$extension"; serial=$((serial + 1))
  done
  used_names[$key]=1; planned+=("$candidate")
done
output="${source_root%/}_compressed"
if [ "$source_root" = / ]; then terminal failed 'Select a subfolder instead of the filesystem root'; exit 0; fi
suffix=2
while case_match "$output" >/dev/null; do output="${source_root%/}_compressed_$suffix"; suffix=$((suffix + 1)); done
event manifest ready
if [ -f "$job_dir/cancel" ]; then cancelled; fi
if [ "$dry" = true ]; then terminal dryRun 'Manifest validated; no output created'; exit 0; fi
if [ "$total" -eq 0 ]; then terminal empty 'No supported images'; exit 0; fi
while ! mkdir -- "$output" 2>/dev/null; do
  if [ ! -e "$output" ] && [ ! -L "$output" ]; then terminal failed 'Cannot create output directory'; exit 0; fi
  output="${source_root%/}_compressed_$suffix"; suffix=$((suffix + 1))
done
event manifest ready
# 并行池：caesiumclt 的 --threads 是作业级并行，单文件调用时多线程无效；
# 这里按 THREADS 并发跑多个单文件作业，才能真正吃满多核（实测 4 核约 3.2 倍）。
max_jobs="${THREADS:-4}"
[[ "$max_jobs" =~ ^[0-9]+$ ]] || max_jobs=4
[ "$max_jobs" -ge 1 ] || max_jobs=1

run_one() {
  local index="$1" file="${files[$1]}" stage="$output/.imgzip-$JOB_ID-$1" log="$job_dir/file-$1.log" result="$job_dir/result-$1"
  local args=(-e --keep-dates --threads 1)
  local extension size_before size_after generated relative relative_dir destination directory_ok leaf target serial rc remaining component matched
  extension=$(printf '%s' "${file##*.}" | tr '[:upper:]' '[:lower:]')
  [ "$extension" != jpg ] || extension=jpeg
  if [ "$FORMAT" != keep ] && [ "$FORMAT" != "$extension" ]; then args+=(--format "$FORMAT"); fi
  if [ "$LOSSLESS" = true ]; then args+=(--lossless); else args+=(-q "$QUALITY"); [ "$MAX_SIZE" -eq 0 ] || args+=(--max-size "$MAX_SIZE"); fi
  if [ "$RESIZE" != none ]; then
    case "$RESIZE" in long) option=--long-edge;; short) option=--short-edge;; width) option=--width;; height) option=--height;; esac
    args+=("$option" "$PIXELS"); [ "$NO_UPSCALE" != true ] || args+=(--no-upscale)
  fi
  size_before=$(stat -c%s -- "$file" 2>/dev/null || printf 0)
  if ! mkdir -- "$stage" 2>/dev/null; then printf 'failed|%s|0|Cannot create staging directory\n' "$size_before" > "$result"; return; fi
  "$engine" "${args[@]}" -o "$stage" "$file" > "$log" 2>&1
  rc=$?
  generated=()
  while IFS= read -r -d '' item; do generated+=("$item"); done < <(find "$stage" -type f -print0)
  if [ "$rc" -ne 0 ] || [ "${#generated[@]}" -ne 1 ]; then
    printf 'failed|%s|0|caesium exit=%s; %s\n' "$size_before" "$rc" "$(tail -c 800 "$log" | tr '\n' ' ')" > "$result"; return
  fi
  relative="${planned[$index]}"; relative_dir=$(dirname -- "$relative")
  destination="$output"; directory_ok=true
  if [ "$relative_dir" != . ]; then
    remaining="$relative_dir"
    while [ -n "$remaining" ]; do
      component="${remaining%%/*}"
      if [ "$remaining" = "$component" ]; then remaining=''; else remaining="${remaining#*/}"; fi
      matched=$(case_match "$destination/$component" || true)
      if [ -n "$matched" ]; then destination="$matched"; else destination="$destination/$component"; fi
      if ! mkdir -p -- "$destination"; then directory_ok=false; break; fi
    done
  fi
  if [ "$directory_ok" != true ]; then printf 'failed|%s|0|Cannot create target subdirectory\n' "$size_before" > "$result"; return; fi
  leaf=$(basename -- "$relative"); target="$destination/$leaf"; serial=2
  while case_match "$target" >/dev/null; do target="$destination/${leaf%.*}_$serial.${leaf##*.}"; serial=$((serial + 1)); done
  size_after=$(stat -c%s -- "${generated[0]}" 2>/dev/null || printf 0)
  # Hard-link promotion is atomic and refuses an existing destination (same filesystem).
  if ln -- "${generated[0]}" "$target"; then printf 'succeeded|%s|%s|\n' "$size_before" "$size_after" > "$result"
  else printf 'failed|0|0|Cannot safely publish output\n' > "$result"; fi
}

declare -A job_of_pid=()
running=0; next=0
while [ "$next" -lt "$total" ] || [ "$running" -gt 0 ]; do
  [ ! -f "$job_dir/cancel" ] || cancelled
  while [ "$running" -lt "$max_jobs" ] && [ "$next" -lt "$total" ]; do
    run_one "$next" &
    job_of_pid[$!]="$next"
    running=$((running + 1)); next=$((next + 1))
  done
  sleep 0.05
  for pid in "${!job_of_pid[@]}"; do
    kill -0 "$pid" 2>/dev/null && continue
    index="${job_of_pid[$pid]}"; unset 'job_of_pid[$pid]'
    running=$((running - 1))
    result="$job_dir/result-$index"
    state='failed'; size_before=0; size_after=0; message='Task did not produce a result'
    if [ -f "$result" ]; then IFS='|' read -r state size_before size_after message < "$result"; fi
    file="${files[$index]}"
    if [ "$state" = succeeded ]; then
      succeeded=$((succeeded + 1)); before=$((before + size_before)); after=$((after + size_after)); message=''
    else
      failed=$((failed + 1)); size_after=0
    fi
    completed=$((completed + 1))
    event file "$state" "$file" "$message" "$index" "$size_before" "$size_after"
    stage="$output/.imgzip-$JOB_ID-$index"; cleanup_stage; stage=''
    rm -f -- "$result"
  done
done
if [ "$failed" -eq 0 ]; then terminal succeeded
elif [ "$succeeded" -gt 0 ]; then terminal partial
else terminal failed; fi
