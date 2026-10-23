#!/bin/sh
# Starts the web image on its own, with no API behind it, and checks what the proxy itself is responsible for: the
# headers, the single page's fallback, caching, compression, and that the SignalR access token never reaches its log.
# It also asks for a request the API would answer, to see what the access log records.
#
#   sh deploy/smoke-test.sh <image>        for example: docker build -f frontend/Dockerfile -t chatnet-web . && sh deploy/smoke-test.sh chatnet-web
#
# Needs docker and curl. Exits with the number of checks that failed.

set -u

image="${1:?usage: smoke-test.sh <image>}"
port="${SMOKE_PORT:-18081}"
name="chatnet-web-smoke-$$"
failures=0

cleanup() {
  docker rm -f "$name" >/dev/null 2>&1 || true
}
trap cleanup EXIT

# Host names on plain http: one that looks like a public site and two that are the machine itself. (TLS is the
# same Caddy either way; what differs between hosts is decided per request, which plain http shows just as well.)
docker run -d --name "$name" -p "$port:80" -e SITE_ADDRESS="http://smoke.example.test, http://localhost, http://127.0.0.1" "$image" >/dev/null || exit 1

tries=0
until curl -s -o /dev/null "http://localhost:$port/"; do
  tries=$((tries + 1))
  if [ "$tries" -gt 30 ]; then
    echo "FAIL: the web server did not start"
    docker logs "$name"
    exit 1
  fi
  sleep 1
done

# The response headers of a request for $2 as host $1, one header per line, names in lower case.
headers() {
  curl -s -D - -o /dev/null -H "Host: $1" ${3:+-H "$3"} "http://localhost:$port$2" | tr -d '\r' | tr 'A-Z' 'a-z'
}

expect_header() { # description, host, path, header line that must be there (lower case), optional request header
  if headers "$2" "$3" "${5:-}" | grep -qF "$4"; then echo "ok    $1"; else echo "FAIL  $1 (missing: $4)"; failures=$((failures + 1)); fi
}

expect_no_header() { # description, host, path, header name that must not be there (lower case)
  if headers "$2" "$3" | grep -q "^$4:"; then echo "FAIL  $1 (found: $4)"; failures=$((failures + 1)); else echo "ok    $1"; fi
}

public=smoke.example.test

expect_header    "the page carries X-Frame-Options"                 "$public" / "x-frame-options: deny"
expect_header    "the page carries nosniff"                         "$public" / "x-content-type-options: nosniff"
expect_header    "the page carries no-referrer"                     "$public" / "referrer-policy: no-referrer"
expect_header    "the page carries a content security policy"       "$public" / "content-security-policy: frame-ancestors 'none'"
expect_header    "the page itself is not cached"                    "$public" / "cache-control: no-cache"
expect_no_header "the server does not name itself"                  "$public" / server
expect_header    "a public host is told to use https (HSTS)"        "$public" / "strict-transport-security: max-age=31536000"
expect_no_header "localhost is not told to use https (HSTS)"        localhost / strict-transport-security
expect_no_header "127.0.0.1 is not told to use https (HSTS)"        127.0.0.1 / strict-transport-security

# A route of the page that is not a file gets the page.
status=$(curl -s -o /dev/null -w '%{http_code} %{content_type}' -H "Host: $public" "http://localhost:$port/chat/00000000-0000-0000-0000-000000000000")
case "$status" in
  "200 text/html"*) echo "ok    a route of the page gets index.html" ;;
  *) echo "FAIL  a route of the page gets index.html (got: $status)"; failures=$((failures + 1)) ;;
esac

# A built file has a hash in its name and is cached for a year.
asset=$(curl -s -H "Host: $public" "http://localhost:$port/" | grep -o '/assets/[^"]*\.js' | head -1)
if [ -z "$asset" ]; then
  echo "FAIL  the page names a built script"; failures=$((failures + 1))
else
  expect_header "a built file is cached for a year" "$public" "$asset" "cache-control: public, max-age=31536000, immutable"
fi

expect_header "responses are compressed" "$public" / "content-encoding: gzip" "Accept-Encoding: gzip"

# The access token SignalR puts in a query string must not be written to the log. No API is behind this proxy, so the
# answer is an error; the line is what matters.
curl -s -o /dev/null -X POST -H "Host: $public" "http://localhost:$port/hubs/chat/negotiate?negotiateVersion=1&access_token=SMOKESECRET"
sleep 1
log=$(docker logs "$name" 2>&1)
if echo "$log" | grep -q "SMOKESECRET"; then
  echo "FAIL  the access token is not in the log (it is)"; failures=$((failures + 1))
elif echo "$log" | grep -q "access_token=REDACTED"; then
  echo "ok    the access token is removed from the log"
else
  echo "FAIL  the request was not logged at all"; failures=$((failures + 1))
fi

if [ "$failures" -eq 0 ]; then echo "all checks passed"; else echo "$failures check(s) failed"; fi
exit "$failures"
