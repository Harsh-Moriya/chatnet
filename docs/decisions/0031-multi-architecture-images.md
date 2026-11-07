# Images for x86-64 and ARM64 from the same Dockerfiles

Status: Accepted

## Context

ARM servers are a cheap place to host this, and the migration bundle was compiled for x86-64 only.

## Decision

The API, migration and web images build for both CPUs from the same Dockerfiles. The CPU comes from the platform being built, the compile stages run on the machine doing the build, so an ARM image built on an x86 machine needs no emulation, and the migration bundle is compiled for the target with `--target-runtime`. Any other CPU stops the build. A script reads the CPU type from the ELF header of an executable in each image, because an image's platform label comes from its base image, and CI runs it.

## Alternatives considered

- A Dockerfile per CPU.
- Building under emulation: the first attempt took 571 s and still failed at the bundle.
- Defaulting an unknown CPU to x86-64.

## Consequences

- An ARM image builds in about a minute on an x86 machine.
- The stack has run as ARM64 only under emulation, not on a real ARM server.

## Related

- [Deployment](../operations/deployment.md)
