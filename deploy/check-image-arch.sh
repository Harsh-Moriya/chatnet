#!/bin/sh
# Reads the CPU type from the header of an executable inside an image, to check that the image holds code for the CPU
# it is meant for. An image's own platform label comes from the base image it starts from, so it can say arm64 while a
# file built into it is for x86-64; this looks at the file itself.
#
#   sh deploy/check-image-arch.sh <image> <executable inside the image> <amd64|arm64>
#
# Needs docker. Exits 0 when the file is an executable for that CPU, 1 when it is not.

set -u

image="${1:?usage: check-image-arch.sh <image> <executable inside the image> <amd64|arm64>}"
file="${2:?usage: check-image-arch.sh <image> <executable inside the image> <amd64|arm64>}"
arch="${3:?usage: check-image-arch.sh <image> <executable inside the image> <amd64|arm64>}"

# The two bytes at offset 18 of an ELF header name the CPU, least significant byte first: 0x3e is x86-64, 0xb7 is AArch64.
case "$arch" in
  amd64) machine="3e00" ;;
  arm64) machine="b700" ;;
  *) echo "unknown architecture: $arch" >&2; exit 2 ;;
esac

container=$(docker create --platform "linux/$arch" "$image" 2>/dev/null) || {
  echo "FAIL  $image: there is no linux/$arch image of that name to look inside"
  exit 1
}
trap 'docker rm -f "$container" >/dev/null 2>&1' EXIT

# An ELF executable starts with 7f 45 4c 46. docker cp writes a tar archive, so the file is taken out of it, and the
# first 20 bytes are enough to read both values. The rest of the stream is read and thrown away: closing it early
# makes docker report a write error.
header=$(MSYS_NO_PATHCONV=1 docker cp "$container:$file" - 2>/dev/null | tar -xO 2>/dev/null | { head -c 20 | od -An -tx1 | tr -d ' \n'; cat >/dev/null; })
magic=$(echo "$header" | cut -c1-8)
found=$(echo "$header" | cut -c37-40)

if [ "$magic" = "7f454c46" ] && [ "$found" = "$machine" ]; then
  echo "ok    $image: $file is for $arch"
else
  echo "FAIL  $image: $file is not an executable for $arch (header: $header)"
  exit 1
fi
