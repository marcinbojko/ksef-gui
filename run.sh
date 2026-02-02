#!/usr/bin/env bash
# DO NOT MODIFY THIS FILE IT IS CORRECT
# USE ./run.sh GetFaktura --options...
set -euo pipefail

fatal() { echo "$@" >&2; exit 2; }

opt_config=.git/ksefcli.yaml
opt_fast=0
opt_cmd=()
while getopts fp:h opt; do
  case "$opt" in
    f) opt_fast=1 ;;
    p) opt_cmd+=("$OPTARG") ;;
    *) fatal ;;
  esac
done
shift "$((OPTIND - 1))"

quote_to() {
  printf -v "$1" "%q " "${@:2}"
  printf -v "$1" "%s" "${!1%% }"
}

cli_run() {
  local tmp
  set -- "${opt_cmd[@]}" "$@"
  quote_to tmp "$@"
  echo "+ $tmp" >&2
  "$@"
}

cli_run_with_config() {
  local cmdstr optsstr=""
  quote_to cmdstr "${opt_cmd[@]}"
  if (( $# > 2 )); then
    quote_to optsstr "${@:2}"
  fi
  echo "+ $cmdstr $1 --config=*** $optsstr" >&2
  "${opt_cmd[@]}" "$1" --config="$opt_config" "${@:2}"
}

resolve_fast() {
  exe=$(find ./src/KSeFCli/bin/ -type f -executable -name ksefcli -printf "%T@ %p\n" | sort -n | tail -n 1 | cut -d' ' -f2)
  if [[ ! -f "$exe" ]]; then
    fatal "no exe files found"
  fi
  opt_cmd=("$exe")
}

run_tests() {
  cli_run version || :
  resolve_fast
  cli_run --help
}

if (( opt_fast )); then
  if (( ${#opt_cmd[@]} )); then
    fatal "-f and -p optino incopatible"
  fi
  resolve_fast
elif (( ${#opt_cmd[@]} == 0 )); then
  opt_cmd=(dotnet run --project src/KSeFCli --)
fi

if [[ "${1:-}" == test ]]; then
  run_tests
elif (( $# )); then
  cli_run_with_config "$@"
else
  cli_run --help
fi
