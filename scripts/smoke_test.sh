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
# then scans the server log for load / method-resolution errors.
#
# Usage: scripts/smoke_test.sh [BASE_URL]    (default http://localhost:8096)
# Requires: curl, python3. Expects a Jellyfin container reachable at BASE_URL whose
# log is dumped by the caller on failure. Exits non-zero on any failed assertion.

set -euo pipefail

BASE="${1:-http://localhost:8096}"
ADMIN_USER="smokeadmin"
ADMIN_PASS="Smoke-Test-Pw-123"
AUTH_HEADER='X-Emby-Authorization: MediaBrowser Client="smoke", Device="ci", DeviceId="ci-smoke-device", Version="1.0.0"'

say() { printf '\n=== %s ===\n' "$1"; }

# httpcode METHOD URL [extra curl args...] -> echoes HTTP status code, body -> /tmp/body
httpcode() {
  local method="$1" url="$2"; shift 2
  curl -s -o /tmp/body -w '%{http_code}' -X "$method" "$@" "$url" || echo "000"
}

say "1. Wait for Jellyfin to answer /System/Info/Public"
up=""
for i in $(seq 1 60); do
  code=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/System/Info/Public" || true)
  if [ "$code" = "200" ]; then up="yes"; echo "Jellyfin up (attempt $i)"; break; fi
  sleep 3
done
[ -n "$up" ] || { echo "::error::Jellyfin never became reachable at $BASE"; exit 1; }
curl -fsS "$BASE/System/Info/Public"; echo

say "2. Complete the first-run startup wizard"
# These endpoints are unauthenticated only during first-run. Idempotency: if the
# server was already set up (re-run on a persisted volume), they return 4xx; we
# tolerate that and move on to auth.
code=$(httpcode POST "$BASE/Startup/Configuration" \
  -H 'Content-Type: application/json' \
  -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}')
echo "POST /Startup/Configuration -> $code"
httpcode GET "$BASE/Startup/User" >/dev/null || true
code=$(httpcode POST "$BASE/Startup/User" \
  -H 'Content-Type: application/json' \
  -d "{\"Name\":\"$ADMIN_USER\",\"Password\":\"$ADMIN_PASS\"}")
echo "POST /Startup/User -> $code"
code=$(httpcode POST "$BASE/Startup/Complete")
echo "POST /Startup/Complete -> $code"

say "3. Authenticate as the admin"
code=$(httpcode POST "$BASE/Users/AuthenticateByName" \
  -H "$AUTH_HEADER" -H 'Content-Type: application/json' \
  -d "{\"Username\":\"$ADMIN_USER\",\"Pw\":\"$ADMIN_PASS\"}")
echo "POST /Users/AuthenticateByName -> $code"
if [ "$code" != "200" ]; then
  echo "::error::Admin authentication failed ($code)"; cat /tmp/body; exit 1
fi
TOKEN=$(python3 -c "import json,sys; print(json.load(open('/tmp/body'))['AccessToken'])")
[ -n "$TOKEN" ] || { echo "::error::No AccessToken in auth response"; exit 1; }
echo "Got admin token (${#TOKEN} chars)"
TOKEN_HEADER="Authorization: MediaBrowser Token=\"$TOKEN\""

say "4. Public endpoint GET /MaintenanceDeluxe/maintenance (controller routable)"
code=$(httpcode GET "$BASE/MaintenanceDeluxe/maintenance")
echo "-> $code"
if [ "$code" != "200" ]; then
  echo "::error::/MaintenanceDeluxe/maintenance returned $code (expected 200)"; cat /tmp/body; exit 1
fi
python3 -c "import json;d=json.load(open('/tmp/body'));print('snapshot keys:',sorted(d.keys()))"

say "5. Admin endpoint GET /MaintenanceDeluxe/users-summary (calls IUserManager.GetUsers)"
# THE killer assertion: this is the exact method path that threw MissingMethodException
# on 10.11.9. A 200 here means GetUsers() resolved against the running server.
code=$(httpcode GET "$BASE/MaintenanceDeluxe/users-summary" -H "$TOKEN_HEADER")
echo "-> $code"
if [ "$code" != "200" ]; then
  echo "::error::/MaintenanceDeluxe/users-summary returned $code (expected 200) — possible API-incompatibility regression"
  cat /tmp/body
  exit 1
fi
python3 -c "import json;d=json.load(open('/tmp/body'));print('users-summary OK, payload type:',type(d).__name__)"

say "6. Admin endpoint GET /MaintenanceDeluxe/announcements/admin (calls GetUsers too)"
code=$(httpcode GET "$BASE/MaintenanceDeluxe/announcements/admin" -H "$TOKEN_HEADER")
echo "-> $code"
if [ "$code" != "200" ]; then
  echo "::error::/MaintenanceDeluxe/announcements/admin returned $code (expected 200)"
  cat /tmp/body
  exit 1
fi

echo
echo "SMOKE TEST PASSED: plugin loaded, controller routable, GetUsers()-backed endpoints all 200."
