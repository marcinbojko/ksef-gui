#!/usr/bin/env bash
. "$(dirname "$(readlink -f "$0")")"/run.sh ${1:+-p"$1"} test
