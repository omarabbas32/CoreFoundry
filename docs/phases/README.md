# CoreFoundry — Phases

Each phase is a vertical slice that ends in something you can run and demo.
Finish a phase's **Definition of done** before starting the next one.

| Phase | Name | Delivers | Depends on |
|---|---|---|---|
| [M0](phase-0-setup.md) ✅ | Setup | Repo, solution skeleton, local MySQL accounts, CI | — |
| [M1](phase-1-auth-projects.md) ✅ | Auth, projects, members | Sign in, create a project (and its `cf_p_<id>` database), manage members | M0 |
| [M2](phase-2-table-designer.md) ✅ | Table designer | Draft tables & columns with full validation, designer UI | M1 |
| [M2.5](phase-2b-relations.md) ✅ | Relations + diagram | Column references (foreign keys) with on-delete rules, schema diagram | M2 |
| [M3](phase-3-schema-engine.md) ⭐ | Schema engine | Plan (diff + SQL preview), Apply, migration history, drift | M2 |
| [M4](phase-4-data-api.md) | Data API | Browse / add / edit / delete rows of generated tables | M3 |
| [M5](phase-5-polish.md) | Portfolio polish | README, demo data, GIF, live deploy | M4 |

**If time gets tight:** M3 > M4 > M2 UI polish. M3 is the differentiator.

## Reference
- [Progress](../PROGRESS.md): current status, verification and known gaps
- [Plan](../../intial-plan.md): scope and decisions log
- [Data model](../corefoundry-erd.html): ERD, physical layout, draft → real mapping
- [Backend flows](../corefoundry-flows.html): layers, auth, authorization, lifecycle, plan/apply, Data API

## Conventions used in every phase
- **Branch per phase** (`m1-auth-projects`), squash-merge to `main` when the Definition of done is met.
- **Every endpoint** returns `ProblemDetails` on error and is covered by at least one API test.
- **No phase is done with failing CI.**
- Decisions made during a phase that change the plan go into §0 of the plan (decisions log).
