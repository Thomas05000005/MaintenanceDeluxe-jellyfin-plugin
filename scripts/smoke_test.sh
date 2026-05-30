#!/usr/bin/env bash
#
# Deployment smoke test: proves the built plugin DLL actually LOADS into a real
# Jellyfin server and that its controller endpoints resolve at runtime against the
# server's assemblies — i.e. it catches the exact bug class that shipped in v0.8.0:
#
#     System.MissingMethodException: Method not found ... IUserManager.get_Users()
#
# That exception only fires when an endpoint that calls IUserManager.GetUsers() is
# actually invoked, so a load-only check is not enough. This script completes the
# first-run wizard, authenticates as the admin, and hits BOTH:
#   - GET /MaintenanceDeluxe/maintenance   (public, proves the controller is routable)
#   - GET /MaintenanceDeluxe/users-summary (admin, calls GetUsers() — the bug method)
# then the caller scans the server log for load / method-resolution errors.
#
# Usage: scripts/smoke_test.sh [BASE_URL]    (default http://localhost:8096)
# Requires: curl, python3. Expects a Jellyfin container reachable at BASE_URL whose
# log is dumped by the caller on failure. Exits non-zero on any failed assertion.
#
# Timing note: a cold Jellyfin container answers GET /System/Info/Public with 200
# while the rest of the app pipeline still returns 503 ("server starting" page) for
# tens of seconds. So readiness is gated on the wizard endpoint actually answering
# 200, not on /System/Info/Public.

set -euo pipefail

BASE="${1:-http://localhost:8096}"
ADMIN_USER="smokeadmin"
ADMIN_PASS="Smoke-Test-Pw-123"
AUTH_HEADER='X-Emby-Authorization: MediaBrowser Client="smoke", Device="ci", DeviceId="ci-smoke-device", Version="1.0.0"'

say() { printf '\n=== %s ===\n' "$1"; }

# code METHOD URL [extra curl args...] -> echoes HTTP status; body saved to /tmp/body
code() {
  local method="$1" url="$2"; shift 2
  curl -s -o /tmp/body -w '%{http_code}' -X "$method" "$@" "$url" 2>/dev/null || echo "000"
}

# wait_for DESC URL WANT [attempts] [extra curl args...] -> 0 when GET URL returns WANT
wait_for() {
  local desc="$1" url="$2" want="$3" attempts="${4:-60}"; shift 4 || shift 3
  local c=""
  for i in $(seq 1 "$attempts"); do
    c=$(curl -s -o /dev/null -w '%{http_code}' "$@" "$url" 2>/dev/null || echo "000")
    if [ "$c" = "$want" ]; then echo "$desc ready (code $c, attempt $i)"; return 0; fi
    sleep 3
  done
  echo "::error::timeout waiting for $desc at $url (wanted $want, last $c after $attempts attempts)"
  return 1
}

say "1. Wait for the first-run wizard to be live (GET /Startup/Configuration == 200)"
# This gates on the FULL app pipeline being up, not just the early /System/Info/Public.
wait_for "first-run wizard" "$BASE/Startup/Configuration" 200 70

say "2. Complete the first-run startup wizard"
c=$(code POST "$BASE/Startup/Configuration" -H 'Content-Type: application/json' \
  -d '{"ServerName":"smoke","UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}')
echo "POST /Startup/Configuration -> $c"
# GET /Startup/User (GetFirstUser) materialises the first-user record that the POST
# below updates. Without it, UpdateStartupUser returns 404 (no user to update).
c=$(code GET "$BASE/Startup/User")
echo "GET /Startup/User -> $c"
c=$(code POST "$BASE/Startup/User" -H 'Content-Type: application/json' \
  -d "{\"Name\":\"$ADMIN_USER\",\"Password\":\"$ADMIN_PASS\"}")
echo "POST /Startup/User -> $c"
[ "$c" = "204" ] || [ "$c" = "200" ] || { echo "  body:"; head -c 400 /tmp/body; echo; }
c=$(code POST "$BASE/Startup/Complete")
echo "POST /Startup/Complete -> $c"

say "3. Wait for the API to be live post-setup, then authenticate as admin (with retries)"
# /System/Info/Public flips to 200 quickly; the auth endpoint may need a moment more.
wait_for "server info" "$BASE/System/Info/Public" 200 40
TOKEN=""
for i in $(seq 1 30); do
  c=$(code POST "$BASE/Users/AuthenticateByName" -H "$AUTH_HEADER" -H 'Content-Type: application/json' \
    -d "{\"Username\":\"$ADMIN_USER\",\"Pw\":\"$ADMIN_PASS\"}")
  if [ "$c" = "200" ]; then
    TOKEN=$(python3 -c "import json;print(json.load(open('/tmp/body'))['AccessToken'])" 2>/dev/null || echo "")
    [ -n "$TOKEN" ] && { echo "Auth OK (attempt $i), token ${#TOKEN} chars"; break; }
  fi
  sleep 3
done
if [ -z "$TOKEN" ]; then
  echo "::error::Admin authentication never succeeded (last code $c)"; cat /tmp/body 2>/dev/null | head -5; exit 1
fi
TOKEN_HEADER="Authorization: MediaBrowser Token=\"$TOKEN\""

say "4. Public endpoint GET /MaintenanceDeluxe/maintenance (controller routable)"
c=$(code GET "$BASE/MaintenanceDeluxe/maintenance")
echo "-> $c"
if [ "$c" != "200" ]; then
  echo "::error::/MaintenanceDeluxe/maintenance returned $c (expected 200)"; cat /tmp/body | head -5; exit 1
fi
python3 -c "import json;d=json.load(open('/tmp/body'));print('snapshot keys:',sorted(d.keys()))"

say "5. Admin endpoint GET /MaintenanceDeluxe/users-summary (calls IUserManager.GetUsers)"
# THE killer assertion: the exact method path that threw MissingMethodException on 10.11.9.
c=$(code GET "$BASE/MaintenanceDeluxe/users-summary" -H "$TOKEN_HEADER")
echo "-> $c"
if [ "$c" != "200" ]; then
  echo "::error::/MaintenanceDeluxe/users-summary returned $c (expected 200) — possible API-incompatibility regression"
  cat /tmp/body | head -5; exit 1
fi
python3 -c "import json;d=json.load(open('/tmp/body'));print('users-summary OK, payload type:',type(d).__name__)"

say "6. Admin endpoint GET /MaintenanceDeluxe/announcements/admin (also calls GetUsers)"
c=$(code GET "$BASE/MaintenanceDeluxe/announcements/admin" -H "$TOKEN_HEADER")
echo "-> $c"
if [ "$c" != "200" ]; then
  echo "::error::/MaintenanceDeluxe/announcements/admin returned $c (expected 200)"; cat /tmp/body | head -5; exit 1
fi

echo
echo "SMOKE TEST PASSED: plugin loaded, controller routable, GetUsers()-backed endpoints all 200."
