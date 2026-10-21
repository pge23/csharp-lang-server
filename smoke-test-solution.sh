#!/usr/bin/env bash
# Smoke test against any solution: ./smoke-test-solution.sh <solution> <symbol> [wait-seconds]
# The wait keeps stdin open while the workspace loads; closing stdin shuts the server down.
set -euo pipefail

if [ $# -lt 2 ]; then
  echo "usage: $0 <solution> <symbol> [wait-seconds]" >&2
  exit 1
fi

SOLUTION="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
SYMBOL="$2"
WAIT="${3:-300}"

cd "$(dirname "$0")"

(
  printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"0.0.0"}}}'
  printf '%s\n' '{"jsonrpc":"2.0","method":"notifications/initialized"}'
  printf '%s\n' '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
  printf '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"symbol_search","arguments":{"name":"%s"}}}\n' "$SYMBOL"
  printf '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"find_implementations","arguments":{"symbol":"%s"}}}\n' "$SYMBOL"
  printf '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"find_references","arguments":{"symbol":"%s"}}}\n' "$SYMBOL"
  sleep "$WAIT"
) | dotnet run --no-build --project CSharpLangServer.csproj -- "$SOLUTION" 2>smoke.solution.stderr.log
