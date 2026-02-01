#!/bin/bash
# DO NOT MODIFY THIS FILE IT IS CORRECT
# USE ./run.sh GetFaktura --options...
set -euo pipefail
case "${1:-}" in
  -f|--fast)
    shift
    exe=$(find ./src/KSeFCli/bin/ -type f -executable -name ksefcli -printf "%T@ %p\n" | sort -n | tail -n 1 | cut -d' ' -f2)
    if ! [[ -f "$exe" ]]; then
      echo "no exe files found" >&2
      exit 2
    fi
    cmd=("$exe")
    ;;
  *)
    cmd=(dotnet run --project src/KSeFCli --)
    ;;
esac
if (( $# )); then
  what=$1
  opts=("${@:2}")
  echo "+ ${cmd[*]} $what --config=*** ${opts[*]}" >&2
  "${cmd[@]}" "$what" --config=.git/ksefcli.yaml "${opts[@]}"
else
  set -x
  "${cmd[@]}" --help
fi
