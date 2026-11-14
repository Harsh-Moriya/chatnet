# Backups

There are **no automatic backups**. This page says what to back up, how to do it by hand and how to restore.

## What holds what

| Data | Where | In a database dump? |
|---|---|---|
| Accounts, conversations, messages (sealed: the dump holds ciphertext, not text), read positions, notifications, refresh-token hashes, push registrations, the outbox | PostgreSQL | yes |
| Attachments and pictures | the object storage bucket | no |
| Who is online, rate-limit counts, SignalR group state | Redis | no, and it does not need one: it is rebuilt as people connect |
| The HTTPS certificate account | Caddy's `caddy-data` volume (production) | no |

The database is the one thing that cannot be rebuilt.

A dump holds sealed messages and the encrypted key backups (`KeyBackups`). It does not hold a key that opens either, but the backups can be attacked offline by trying passphrases, so a dump is secret: store and copy it as carefully as the database itself.

## Taking a dump

From the repository root, on the machine that runs the production stack:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.prod exec -T postgres \
  sh -c 'pg_dump -U "$POSTGRES_USER" "$POSTGRES_DB"' | gzip > chatnet-$(date +%F).sql.gz
```

Copy the file off the machine. A backup that lives only on the machine it protects is not a backup. Run it on a schedule (cron or a systemd timer), and take one before every update that includes migrations.

For the development database, use the same command with `docker exec chatnet-postgres pg_dump -U chatnet_user chatnet`.

## Restoring

A dump is plain SQL that creates the tables, so it restores into an **empty** database. Restoring over a database that already has the schema fails with errors such as `relation "AspNetRoleClaims" already exists`. The production database is not empty after the migration job has run, so drop and recreate it first. Stop the API instances so nothing writes while you restore:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.prod stop api
docker compose -f docker-compose.prod.yml --env-file .env.prod exec -T postgres \
  sh -c 'dropdb -U "$POSTGRES_USER" "$POSTGRES_DB" && createdb -U "$POSTGRES_USER" "$POSTGRES_DB"'
gunzip -c chatnet-YYYY-MM-DD.sql.gz | docker compose -f docker-compose.prod.yml --env-file .env.prod exec -T postgres \
  sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 -q'
docker compose -f docker-compose.prod.yml --env-file .env.prod start api
```

`ON_ERROR_STOP=1` makes the restore stop at the first error, so a partial restore is not mistaken for a complete one. `dropdb` deletes the current data: take a fresh dump first if there is anything in it you might need.

A restore carries the migration history (`__EFMigrationsHistory`), so the `migrate` job on the next update applies only migrations newer than the dump. Refresh tokens are in the dump: people signed in at the time of the dump stay signed in, and tokens revoked since then become valid again, which includes the sessions of devices that were removed after the dump was taken.

A restore to an older state also takes the keys back: conversation key periods and key generations made since the dump are gone from the server while browsers still hold them. A browser that is ahead of the server is refused with `changes-cursor-ahead` when it follows a conversation, and a person who made a new key generation since finds the server one behind (`generation-stale`). Start over from the people affected rather than restore over keys that were in use.

## Files

Files are in the bucket, not in the dump. A message restored from a dump points at the files that were in the bucket then; a file removed since (a deleted message's files, a replaced picture) is gone, and a file added since has no message. Use the storage provider's own features for bucket versioning or replication if you need files to be recoverable.

## Caddy's data volume

Keep `caddy-data` if you can: it holds the certificate account, and certificate authorities limit how often new certificates may be requested. Losing it is recoverable but can delay HTTPS after a rebuild.
