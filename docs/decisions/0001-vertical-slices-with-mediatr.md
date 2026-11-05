# Vertical slices with MediatR

Status: Accepted

## Context

A chat backend has many small use cases (send, edit, add a member, mark read). Splitting them across technical layers puts one use case in four projects and adds interfaces and mapping that carry no logic at this size.

## Decision

The backend is one project organised by feature. An endpoint binds the request and sends a command or query through MediatR; one handler per use case holds the logic; a FluentValidation validator runs first through a pipeline behaviour; a feature repository wraps data access. A request that returns nothing is declared `IRequest<Unit>`, because the validation behaviour attaches only to requests with a response type, and a test checks that every request with a validator is validated.

## Alternatives considered

- Clean Architecture with a four-project split: more indirection than a project of this size repays.
- Validating inside each handler: invalid input could reach logic that forgets to check.

## Consequences

- A reader follows one use case in one folder.
- The MediatR dependency and pipeline behaviours become part of every request.
- Cross-feature reuse needs care: shared code lives in `Common/`.

## Related

- [Request flow](../architecture/request-flow.md)
- [Architecture overview](../architecture/overview.md)
