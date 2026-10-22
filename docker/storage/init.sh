#!/bin/sh
# ##### Storage bucket setup #####
# Runs once per "docker-compose up" after the storage server is healthy. Every step is safe to repeat:
# the bucket is only created when it is missing, and the two configurations are replaced with the same
# content each time.
set -eu

ENDPOINT="http://rustfs:9000"

if aws --endpoint-url "$ENDPOINT" s3api head-bucket --bucket "$STORAGE_BUCKET" >/dev/null 2>&1; then
  echo "Bucket $STORAGE_BUCKET already exists"
else
  aws --endpoint-url "$ENDPOINT" s3api create-bucket --bucket "$STORAGE_BUCKET"
  echo "Created bucket $STORAGE_BUCKET"
fi

# The browser uploads straight to the bucket, so the bucket has to answer the cross-origin preflight.
aws --endpoint-url "$ENDPOINT" s3api put-bucket-cors \
  --bucket "$STORAGE_BUCKET" --cors-configuration file:///init/cors.json

# Uploads that were never attached to a message are removed after a day.
aws --endpoint-url "$ENDPOINT" s3api put-bucket-lifecycle-configuration \
  --bucket "$STORAGE_BUCKET" --lifecycle-configuration file:///init/lifecycle.json

echo "Storage is ready"
