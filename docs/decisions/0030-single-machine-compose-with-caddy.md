# One Compose file for one machine, with Caddy in front

Status: Accepted

## Context

The first target is a single small server, and a stack proven on one machine moves to any similar host without changes.

## Decision

Production is one Compose file for one host: Caddy (the only published service), the API instances, a one-shot migration job, Postgres and Redis. Caddy serves the built frontend, proxies `/api`, `/hubs` and `/health` to the API instances (found through DNS every 5 s, health-checked, with a cookie that keeps a browser on one instance), and obtains and renews certificates itself. The page and the API share one origin, so the `SameSite=Strict` cookie stays first-party and no CORS is needed. The database is migrated by an EF migration bundle that runs as a job before the API starts; the API does not migrate itself. The API and Caddy containers drop all capabilities, and the API container is read-only.

## Alternatives considered

- Kubernetes or a managed container platform: far more than one machine needs.
- nginx with certbot (two programs and a renewal job) or Traefik.
- Serving the page from the API.
- `Database.Migrate()` at startup: several instances starting together would race to change the schema.

## Consequences

- An update replaces the API instances together, with a short break; there are no rolling updates.
- One Postgres and one Redis, with no replication or automatic backups.

## Related

- [Deployment](../operations/deployment.md)
