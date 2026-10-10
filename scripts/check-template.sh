#!/usr/bin/env bash
# The profile template (#127) must keep producing a solution that builds, passes its tests and validates against the shell's own
# profile parser. Uses a private template hive, so nothing is installed for the user.
set -euo pipefail
cd "$(dirname "$0")/.."
root="$PWD"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
dotnet new install ./templates/lots-profile --debug:custom-hive "$work/hive" >/dev/null
(cd "$work" && dotnet new lots-profile -n Check.Template --profile check-template --debug:custom-hive "$work/hive" >/dev/null)
if grep -rqi "acme" "$work/Check.Template" --exclude-dir=bin --exclude-dir=obj; then
  echo "FAIL: template output still mentions Acme" >&2
  exit 1
fi
dotnet test "$work/Check.Template" --nologo -v q >/dev/null
dotnet run --project "$root/src/Lots.Ctl" --no-build -- validate -f "$work/Check.Template/profiles"
echo "template ok"
