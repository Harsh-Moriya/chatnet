#!/bin/sh
# Writes the configuration the TURN server runs with and starts it. The secret comes from the environment, so it is on neither the
# command line nor in a file of the repository: the file made here lives in /tmp (a tmpfs in production) and is readable only by
# the server's own user.
#
#   turnserver.conf   always (the settings every deployment shares)
#   hardened.conf     only where it is mounted (production): the denied peer ranges and the limits
#   TURN_SECRET       required: the secret the API makes credentials with (Turn:Secret)
#   TURN_EXTERNAL_IP  "public/private" for a machine behind NAT (a cloud VM), the address the relay is reached on and the one it is
#                     bound to. The server allows peers at its own private address by itself when it is given both, which two people
#                     who both need the relay depend on: they reach each other through it, at the public address.
set -eu

: "${TURN_SECRET:?set TURN_SECRET}"

umask 077
conf=/tmp/turnserver.conf

cat /etc/chatnet-turn/turnserver.conf > "$conf"
if [ -f /etc/chatnet-turn/hardened.conf ]; then
  cat /etc/chatnet-turn/hardened.conf >> "$conf"
fi
printf 'static-auth-secret=%s\n' "$TURN_SECRET" >> "$conf"

if [ -n "${TURN_EXTERNAL_IP:-}" ]; then
  printf 'external-ip=%s\n' "$TURN_EXTERNAL_IP" >> "$conf"
fi

exec turnserver -c "$conf"
