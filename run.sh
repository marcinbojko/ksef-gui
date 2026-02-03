#!/usr/bin/env bash
# DO NOT MODIFY THIS FILE IT IS CORRECT
# USE ./run.sh GetFaktura --options...
set -euo pipefail

fatal() { echo "$@" >&2; exit 2; }

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

resolve_fast() {
  local exe
  exe=$(
    find ./src/KSeFCli/bin/ -type f -executable -name ksefcli -exec stat -c '%Y %n' {} + |
      sort -n | tail -n 1 | cut -d' ' -f2-
  )
  if [[ ! -f "$exe" ]]; then
    fatal "no exe files found"
  fi
  opt_cmd=("$exe")
}

###############################################################################

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

if (( opt_fast )); then
  if (( ${#opt_cmd[@]} )); then
    fatal "-f and -p optino incopatible"
  fi
  resolve_fast
elif (( ${#opt_cmd[@]} == 0 )); then
  opt_cmd=(dotnet run --project src/KSeFCli --)
fi

maybe=$PWD/.git/KSEF/ksefcli.yaml
if [[ -r $maybe && ! -v KSEFCLI_CONFIG ]]; then
  export KSEFCLI_CONFIG=$maybe
fi

if (( $# )); then
  cli_run "$@"
else
  cli_run --help
fi
