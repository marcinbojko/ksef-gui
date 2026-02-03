#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
if (( !$# )); then
  set -- dotnet run --project src/KSeFCli --
fi
exe=("$@")

set -x

"${exe[@]}" version || :

"${exe[@]}" --help

# Test with cert_test profile (from file paths)
env KSEFCLI_CONFIG="tests/test_ksefcli.yaml" "${exe[@]}" PrintConfig --active cert_test

# Test with token_test profile
env KSEFCLI_CONFIG="tests/test_ksefcli.yaml" "${exe[@]}" PrintConfig --active token_test

# Test with cert_env_password_test profile
env KSEF_TEST_PASSWORD_ENV="env_password" KSEFCLI_CONFIG="tests/test_ksefcli.yaml" "${exe[@]}" PrintConfig --active cert_env_password_test

# Test with cert_inline_test profile
env KSEFCLI_CONFIG="tests/test_ksefcli.yaml" "${exe[@]}" PrintConfig --active cert_inline_test
