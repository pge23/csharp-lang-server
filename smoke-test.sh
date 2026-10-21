#!/usr/bin/env bash
# Smoke test: spawns the MCP server, performs MCP handshake, calls find_references on "Greeter".
set -euo pipefail

cd "$(dirname "$0")"

SOLUTION="samples/HelloLib.sln"

(
  # initialize
  printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"0.0.0"}}}'
  # initialized notification
  printf '%s\n' '{"jsonrpc":"2.0","method":"notifications/initialized"}'
  # list tools
  printf '%s\n' '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
  # call find_references on Greeter
  printf '%s\n' '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"find_references","arguments":{"symbol":"Greeter"}}}'
  # call symbol_search for IClock (exact)
  printf '%s\n' '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"symbol_search","arguments":{"name":"IClock"}}}'
  # call find_implementations for IClock
  printf '%s\n' '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"find_implementations","arguments":{"symbol":"IClock"}}}'
  # pattern search: substring "Clock" should match IClock + SystemClock + _clock field
  printf '%s\n' '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"symbol_search","arguments":{"name":"Clock","patternMatch":true}}}'
  # pattern search: camelCase hump "SC" should match SystemClock
  printf '%s\n' '{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"symbol_search","arguments":{"name":"SC","patternMatch":true}}}'
  # keep stdin open long enough for the server to handle the calls
  sleep 5
) | dotnet run --no-build --project CSharpLangServer.csproj -- "$SOLUTION" 2>smoke.stderr.log
