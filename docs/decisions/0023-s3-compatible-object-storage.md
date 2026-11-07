# S3-compatible object storage

Status: Accepted

## Context

Attachments and pictures need durable storage that is cheap, reachable with presigned links, and runnable locally.

## Decision

Files are kept in an S3-compatible bucket through the AWS SDK. It is designed for Cloudflare R2 in production, and development and the tests use RustFS. The API depends on a small `IBlobStorage` interface. The local server was chosen after running candidates through a scripted check of the S3 operations the application uses.

## Alternatives considered

- Azure Blob Storage with Azurite or MinIO locally: no longer the target.
- Garage, a community MinIO fork, or LocalStack (which needs an account).
- Running the tests against R2 itself: not hermetic and needs credentials in CI.

## Consequences

- The storage host must be reachable from browsers as well as the server, because the same address is signed into links.
- The bucket needs a CORS rule and a lifecycle rule for `pending/`.

## Related

- [Attachments](../features/attachments.md)
- [Configuration](../operations/configuration.md)
