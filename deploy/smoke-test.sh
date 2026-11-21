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

# A built file that is not there is a 404 and is not kept, and is not answered with the page: a tab that was open across an update asks for
# files by their old names, and html under a script's address fails to run with an error that says nothing.
missing=$(curl -s -o /dev/null -w '%{http_code}' -H "Host: $public" "http://localhost:$port/assets/not-built-0000.js")
if [ "$missing" = "404" ]; then echo "ok    a built file that is not there is a 404"; else echo "FAIL  a built file that is not there is a 404 (got: $missing)"; failures=$((failures + 1)); fi
expect_header    "a built file that is not there is not kept"       "$public" /assets/not-built-0000.js "cache-control: no-store"

expect_header "responses are compressed" "$public" / "content-encoding: gzip" "Accept-Encoding: gzip"

# The service worker is fetched by the browser from a fixed address, and a copy kept in a cache would keep a fix to it from
# reaching browsers that already have one. Were the file missing, the page's fallback would answer with html, which a browser
# refuses to run as a worker, so the type is checked too.
expect_header "the service worker is served as a script" "$public" /sw.js "content-type: text/javascript"
expect_header "the service worker is not cached"         "$public" /sw.js "cache-control: no-cache"

# It is the worker the build makes (the second build, vite.sw.config.ts, into the same folder as the page) and not a stale file: it plays
# encrypted video from /file-stream/ and still shows alerts, and it is one classic script, which a browser runs from this address.
worker=$(curl -s -H "Host: $public" "http://localhost:$port/sw.js")
if echo "$worker" | grep -q "file-stream" && echo "$worker" | grep -q "notificationclick"; then
  echo "ok    the service worker is the built one: it plays video and shows alerts"
else
  echo "FAIL  the service worker is the built one (it does not name file-stream and notificationclick)"; failures=$((failures + 1))
fi
if echo "$worker" | grep -Eq '(^|[;{}])(import|export)[ {*]'; then
  echo "FAIL  the service worker is one classic script (it has an import or export)"; failures=$((failures + 1))
else
  echo "ok    the service worker is one classic script"
fi

# The app's icons and manifest. A file that is not there is answered with the page (the fallback for routes), so a missing icon would
# show as html where a picture is expected, and a browser that cannot read the manifest silently does not offer to install the app.
page=$(curl -s -H "Host: $public" "http://localhost:$port/")
if echo "$page" | grep -q 'rel="manifest"' && echo "$page" | grep -q 'rel="apple-touch-icon"'; then
  echo "ok    the page names the manifest and the touch icon"
else
  echo "FAIL  the page names the manifest and the touch icon"; failures=$((failures + 1))
fi
expect_header "the manifest is served as json" "$public" /site.webmanifest "content-type: application/"
# Safari, link previews and some tools ask for this address by name, and without the file the page's fallback would answer with html.
expect_header "favicon.ico is served as a picture, not as the page" "$public" /favicon.ico "content-type: image/"
for icon in icon-192.png icon-512.png icon-maskable-512.png apple-touch-icon.png badge-96.png; do
  expect_header "the icon $icon is served as a picture" "$public" "/icons/$icon" "content-type: image/png"
done
if curl -s -H "Host: $public" "http://localhost:$port/site.webmanifest" | grep -q '"display": "standalone"'; then
  echo "ok    the manifest is the built one"
else
  echo "FAIL  the manifest is the built one (it does not say display standalone)"; failures=$((failures + 1))
fi

# The PDF viewer's worker is a module worker, which a browser refuses to start unless it is served with a script type, and what pdf.js reads
# while it draws (the character maps and the image decoders) is built as files like the rest. Only the viewer's own script names them, so
# they are found in the image. Their absence would show as a PDF that does not open, or as a Japanese one with the wrong glyphs.
assets=$(docker exec "$name" sh -c 'ls /srv/assets')
worker=$(echo "$assets" | grep '^pdf\.worker\..*\.mjs$' | head -1)
if [ -z "$worker" ]; then
  echo "FAIL  the build has the PDF worker"; failures=$((failures + 1))
else
  expect_header "the PDF worker is served as a script"  "$public" "/assets/$worker" "content-type: text/javascript"
  expect_header "the PDF worker is cached for a year"   "$public" "/assets/$worker" "cache-control: public, max-age=31536000, immutable"
  # The security notes say pdf.js runs no code made from a document (no eval, no new Function): a version that did would be a way into the
  # page with the keys, since the policy the site sends does not restrict scripts.
  if curl -s -H "Host: $public" "http://localhost:$port/assets/$worker" | grep -Eq 'new Function\(|[^A-Za-z0-9_.$]eval\('; then
    echo "FAIL  the PDF worker makes no code from a document (it has eval or new Function)"; failures=$((failures + 1))
  else
    echo "ok    the PDF worker makes no code from a document"
  fi
fi
for pattern in '^UniJIS-UCS2-H-.*\.bcmap$' '^openjpeg-.*\.wasm$' '^jbig2-.*\.wasm$'; do
  file=$(echo "$assets" | grep "$pattern" | head -1)
  if [ -z "$file" ]; then
    echo "FAIL  the build has a file of pdf.js matching $pattern"; failures=$((failures + 1))
  else
    expect_header "a file of pdf.js is served and cached for a year ($file)" "$public" "/assets/$file" "cache-control: public, max-age=31536000, immutable"
  fi
done

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
