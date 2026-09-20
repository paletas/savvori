# Model-assisted matching and categorisation: plan

Status: **approved. Phase 1 implemented; Phase 2 awaiting go-ahead.**

## Principles (from the brief)

- The model is remote and may be down. Every model-backed feature only adds to what the app does today.
- Only background jobs call the model. Scraping, product pages, optimisation and admin actions never do.
- Everything is behind `Model:Enabled` (default `false`). Thresholds, URLs, model names and timeouts are all configuration.
- Manual decisions always win, and model decisions are reversible and carry method, score, model id and timestamp.

## What exists today (relevant facts)

- `StoreProduct` (per chain) links to canonical `Product` via `CanonicalProductId`, with `MatchStatus`, `MatchMethod` and `MatchedAt`.
- `ProductMatcher` does EAN and brand+name+size+unit tiers. It stays untouched (Tier A).
- `ScraperResultProcessor` saves products and creates canonicals. Quartz runs `StoreScrapeJob` per chain.
- Admin/Mapping already shows stats, including `ByMatchMethod`. The API is `MappingAdminController` and the page uses `SavvoriApiClient`.
- Migrations are applied at startup after a DB backup, and there is a single `InitialCreate`.

## Cross-cutting design

**Configuration** (`Model` section, bound to `ModelOptions`):
- `Enabled=false`
- `BaseUrl`, `EmbeddingModel="bge-m3"`, `JudgeModel="qwen2.5:7b-instruct"`
- `ConnectTimeoutSeconds`, `RequestTimeoutSeconds`, `BatchSize`, `MaxConcurrency`
- `Breaker:FailureThreshold`, `Breaker:CooldownSeconds`
- `Queue:MaxAttempts`, `Queue:BaseDelaySeconds`, `Queue:MaxDelaySeconds`
- Later phases add the matching and classifier thresholds, with the defaults from the brief.

**Interfaces** (in `Savvori.WebApi/Modeling`):
- `IEmbeddingClient`: `EmbedAsync(IReadOnlyList<string>) -> EmbeddingResult { vectors, modelName, modelDigest, dimension }`, plus `PingAsync`.
- `IPairJudge`: `JudgeAsync(a, b) -> JudgeVerdict { Yes | No | Unclear }`. A transport failure throws `ModelUnavailableException` and is never mapped to `No`.
- `OllamaEmbeddingClient` uses `/api/embed`. `OllamaPairJudge` uses `/api/chat` with temperature 0 and no JSON mode, and parses a one-word answer defensively (anything that isn't clearly yes or no is `Unclear`). The digest comes from `/api/tags` (the model's entry), cached for 10 minutes.
- Both go through one typed `HttpClient` with a connect timeout (`SocketsHttpHandler.ConnectTimeout`) and a total timeout. They use no standard resilience handler: the retry lives in the queue, so there are no hidden retries and none are unbounded.

**Circuit breaker** (`ModelCircuitBreaker`, singleton, hand-written and small, thread-safe):
- States are Closed, Open and HalfOpen. N consecutive failures or timeouts open it.
- While Open, no calls are made. After the cool-down, one probe (`PingAsync`) runs, and success closes it.
- It takes an injected `TimeProvider` so tests can advance time.
- It is in-memory. After a restart it starts Closed and reopens after N failures. That's acceptable for a single-instance deployment.

**Job queue** (DB table `ModelJobs`): one row per unit of work.
- Columns: `Id`, `Type` (`Embed`, later `Judge`), `SubjectId`, `PayloadHash`, `Status` (Pending, Running, Done, DeadLetter), `Attempts`, `NextAttemptAt`, `LastError`, `CreatedAt`, `UpdatedAt`.
- A unique index on (`Type`, `SubjectId`, `PayloadHash`) filtered to non-Done rows makes enqueue idempotent.
- The Quartz `ModelQueueDrainJob` runs every minute and does nothing when the flag is off or the breaker is Open. It claims a small batch of due jobs, calls the client with capped concurrency, and on failure sets `NextAttemptAt` with exponential backoff and jitter. After `MaxAttempts` a job goes to DeadLetter, and an admin can requeue it.
- Running jobs stuck longer than a lease timeout are reclaimed. This covers a restart mid-batch.

**Degraded mode**: nothing blocks. Products needing embeddings are `EmbeddingPending`, the scraper saves them as today, and nothing model-dependent is accepted.

## Phase 1: Foundation and observability

Deliverables:
1. `ModelOptions`, the two interfaces, the Ollama implementations, `ModelCircuitBreaker`, and DI registration behind the flag. The real clients are always registered but nothing invokes them with the flag off; only the drain job is gated, and it exits immediately.
2. `ModelJobs` table and `ModelQueueDrainJob`. In Phase 1 the only job type is `Embed`, and the handler is a stub that has no consumer yet (see the note below).
3. `IModelStatusService` (breaker state, last success, last error, queue depth, oldest pending job, stale embeddings count), exposed at `GET /api/admin/model/status` and as a panel on Admin/Mapping. The stale count is 0 until Phase 2 adds embeddings, but the query and its tests exist now.
4. Fakes in the test project: `FakeEmbeddingClient` (deterministic hash-based vectors) and `FlakyModelClient` (simulated timeouts, 5xx, slow responses, and "down for a while" driven by `TimeProvider`).
5. Tests:
   - Scraping succeeds and stores products when the client is down or throwing.
   - The breaker opens after N failures, stays open through the cool-down, and closes after a successful probe.
   - Stale embeddings are detected (model or text-hash change) using the metadata check that Phase 2 uses.
   - Queue idempotency, backoff, and dead-lettering.
   - Ollama response parsing, including messy judge answers.

**Migration for Phase 1 (to approve before I apply it).** It creates one new table, `ModelJobs`, with the columns above and two indexes. The first is the filtered unique index for idempotency. The second is on (`Status`, `NextAttemptAt`) for draining. It doesn't touch any existing table, so it is additive and reversible with `Down()`. Existing data is unaffected. The startup path already backs up the DB before migrating. I'll generate it, show you the SQL (`dotnet ef migrations script`) and apply it only after you agree. If you'd rather not migrate in Phase 1, the alternative is to move `ModelJobs` into Phase 2's migration. Then Phase 1 would have no durable queue, and I'd advise against that.

Note on the embedding metadata: I'm putting the `Embedding` entity in Phase 2, not Phase 1, so the Phase 1 migration stays small. The staleness check is written and unit-tested against an interface in Phase 1.

## Phase 2: Embeddings and candidates (outline; detailed after Phase 1 review)

- **Storage: per `StoreProduct`, not per canonical.** The embedding describes one chain's listing text (`"{brand} {name}"`, lower-cased, brand omitted if already in the name). A canonical can be merged, split or unmerged under manual decisions, and a canonical's vector would then need recomputing and would mix texts. Per-StoreProduct vectors are stable and match how candidates are generated (nearest neighbour from a *different chain*). Table `StoreProductEmbeddings`: `StoreProductId` (PK), `Vector` (float32 BLOB), `ModelName`, `ModelDigest`, `Dimension`, `InputTextHash`, `CreatedAt`. Rows are stale when the model name or digest differs from the current one, or the hash differs from the hash of the current text.
- Watch-out from Phase 1: a `ModelResponseException` (server answers with unusable output) counts as a breaker *success*, so a model that is loaded but broken keeps the breaker closed and would dead-letter the catalogue. Phase 2 should add an alarm (e.g. dead-letter rate in the status panel) or a separate response-failure counter.
- An `EmbeddingPending` state is derived rather than stored: a StoreProduct with no fresh embedding row and a pending queue job. This avoids a status column on the hot scraping table.
- The in-memory index is a lazy `float[]` matrix (~18k x 1024 x 4 B ≈ 75 MB), L2-normalised so cosine is a dot product. It is refreshed incrementally by `Version`/`CreatedAt`. Top-8 search is brute force, filtered to other chains, and ignores rows from other models.
- A post-scrape or nightly job enqueues embed jobs for new or changed products. It then generates candidates with the size and brand filters from the brief and stores them in `MatchCandidates` (pair, cosine, `SizeKnown`, `BrandCheck`, model id). It works on whatever is embedded, so a partial catalogue gives fewer but correct candidates.
- Migration: `StoreProductEmbeddings` and `MatchCandidates`, described before applying.

## Phase 3: Tiered matching and review queue (outline)

- Tier A is unchanged. Tier B auto-accepts by cosine at 0.90 / 0.95 when the brand check passes. Tier C asks the judge in the 0.80 / 0.85 to accept-threshold band, and a failed or timed-out call leaves the pair pending. Tier D is the review queue: side-by-side listings, images, size, price, and accept / reject / different-variant buttons. Rejections are stored permanently.
- Merging never combines two canonicals that both have prices from the same chain without explicit confirmation. Groups larger than 2 are handled by merging pairwise through this same check.
- Decisions store method, score, model id and timestamp, and are reversible (unmerge restores the original canonical). Manual decisions are never overwritten by a job.
- The match report on Admin/Mapping is split by method, and a dry-run mode writes proposals to the review queue only (default on for the first run).

## Phase 4: Categories (outline)

- The first deliverable is a taxonomy v2 document only (about 12 aisles, ~80 leaves, tags for Bio / sem lactose / sem glúten, a split of broad categories, and a mapping from every current category). I'll stop there for your approval and won't migrate until you agree.
- Then: reversible label migration (old category id kept), a kNN classifier (k=7, similarity-weighted, auto-assign at ≥0.85, review queue at 0.5–0.85), the existing `CategoryMapper` kept as the degraded-mode path, and store-category strings decided once and cached per string.

## Verification and caveats

- Every phase ends with `dotnet test Savvori.sln`, with `LiveScraperTests` reported separately as the known flake.
- I can't verify against your real Ollama or your 18k-product beta data from here. Phase 1 tests use fakes and fake HTTP handlers. The real-model numbers (precision, recall, before/after match report) can only come from a run on your beta, and I'll say so in each report.
- Threshold defaults were tuned on 180 hand-labelled pairs and must be re-checked against review-queue results.

## Decisions I'll make unless you object

- The circuit breaker state is in-memory only.
- No new NuGet packages. Polly is not used for model calls, because the queue owns retries.
- New code lives in `Savvori.WebApi/Modeling/`. The entities go in `Savvori.Shared`.
- Docs (`FUNCTIONAL_REQUIREMENTS.md`, `TECHNICAL_ARCHITECTURE.md`) are updated in each phase.

## Phase 1 report

Commit: `feat: model backend foundation (breaker, job queue, status) behind a feature flag`.

**Verified**
- `dotnet test Savvori.sln --filter "FullyQualifiedName!~LiveScraperTests"`: 407 passed, 0 failed. `LiveScraperTests` were excluded and not run.
- 63 of those are the new modelling tests: breaker open/close/probe, guarded clients, queue idempotency/backoff/dead-letter/lease reclaim, drain job with the model down and recovering, no dead-lettering during a long outage, Ollama request/response handling and verdict parsing, stale-embedding detection, status endpoint.
- Scraping completes and stores products (auto-matched as before) while the model is down and jobs are queued.
- The `AddModelJobs` migration applied cleanly to a throwaway SQLite file (not your database): `ModelJobs` plus both indexes created, both migrations recorded in `__EFMigrationsHistory`. It is applied to the real DB by the normal startup path, after the existing backup.

**Not verified**
- No real Ollama and none of your beta data. The `/api/embed`, `/api/tags` and `/api/chat` shapes are only exercised against stub handlers, so first contact with the real server is unvalidated (including whether the model name in `/api/tags` matches `Model:EmbeddingModel` exactly, with or without `:latest`).
- The Admin/Mapping panel was compiled but not viewed in a browser.

**Before/after match report:** unchanged by design. Phase 1 touches no matching or categorisation path, and all model features are off (`Model:Enabled=false`).

**Deviations from the approved plan**
- Real Ollama clients are always registered (nothing calls them with the flag off) instead of a no-op client; only the drain job is gated.
- Model digest is read from `/api/tags`, not `/api/show` (the plan text is corrected above).
- The build regenerated the tracked `wwwroot/css/site.css` (Tailwind picked up the new panel's classes), so it is in the commit even though `.gitignore` lists it; it was already tracked before this work.

**Threshold caveat:** the defaults planned for later phases (0.90 / 0.95 accept, 0.80 / 0.85 judge band, 0.85 category auto-assign) were tuned on a small hand-labelled sample and must be re-checked against review-queue results.

**To enable:** set `Model:Enabled=true` (and check `Model:BaseUrl`) in configuration.
