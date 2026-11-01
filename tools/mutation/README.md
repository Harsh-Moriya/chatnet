# Mutation checks

A mutation check breaks one line of the code on purpose (a changed condition, a deleted call) and runs the tests that are meant to notice. A test suite that stays green over a broken rule does not test that rule. `mutate.cjs` runs many such checks side by side.

## Why it is built this way

- **The real working tree is never edited.** Each worker has its own copy of `backend/` and `frontend/` in a work directory (by default `chatnet-mutation` under the system temp folder). An earlier version edited the tree in place; a commit made during a run captured a broken line (`a44d93e`, fixed in `c8cf9b9`).
- **Mutants run in parallel**, 3 workers by default. Measured on 2026-11-01 (Ryzen 5 5500U, 6 cores, Docker with 12 CPUs and 7 GiB): 12 backend mutants took about 146 s with 3 workers, and 6 workers were no faster, so more workers do not help on that machine.
- **Each mutant lists its tests in stages**, cheapest first, and stops at the first stage that fails. A mutant is dead as soon as one test fails, so the single test aimed at the rule goes first and the whole class or suite only runs if that one passes.

## Running it

Docker must be running (the backend tests start their own containers). From the repository root:

```bash
node tools/mutation/mutate.cjs tools/mutation/example.spec.cjs
node tools/mutation/mutate.cjs my.spec.cjs --workers 3 --out results.jsonl
```

`--work <directory>` moves the worker copies. The first run restores and builds every worker (about a minute); later runs only compile what changed. The run ends by saying whether the real working tree is unchanged. Commit first: the workers copy the tree as it is, uncommitted changes included, and say so if there are any.

## The spec

A spec is a CommonJS module that exports an array:

```js
{
  name: 'typing-gate-kept-after-removal',
  area: 'be',                                   // 'be' for backend, 'fe' for frontend
  file: 'backend/ChatNET.API/.../ConnectionEvictor.cs',   // repository-relative
  old: '                ConnectionState.Forget(connection.Items, conversationId);\n',
  new: '',
  stages: ['FullyQualifiedName~SomeTest', 'FullyQualifiedName~SomeTestClass'],
}
```

- `old` must occur exactly once in the file; otherwise the verdict is `NOAPPLY`. Include enough surrounding text to make it unique.
- Backend stages are `dotnet test --filter` expressions. Frontend stages are arrays of test files relative to `frontend/`; an empty array runs the whole suite.

## Verdicts

| Verdict | Meaning |
|---|---|
| `KILLED` | a stage failed: the tests notice the change |
| `SURVIVED` | every stage passed: no test notices. Add a test, or show the mutant is equivalent (it changes no behaviour) and say so |
| `COMPILE` | the broken code does not build. Rewrite the mutant so that it does |
| `NOAPPLY` | `old` was not found exactly once |
| `INCONCLUSIVE` | a stage gave no verdict (for example the filter matched no test). Fix the stage |

## Choosing mutants

Mutate what a bug would hurt: rules, authorization, SQL, locking and ordering, the places where a wrong condition is plausible. Do not mutate plain wiring or field copying; the compiler and ordinary tests cover those. Around 40 to 50 well-chosen mutants per branch say more than 100 near-duplicates.
