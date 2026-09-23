# Model-assisted matching and categorisation: plan

Status: **approved. Phases 1, 2 and 3 implemented; Phase 4: taxonomy v2 approved and implemented (explicit admin apply/revert, not yet applied to your database); the classifier is implemented and ships in dry run.**

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

## Phase 2 report

**What it does.** Embeddings are stored per StoreProduct (`StoreProductEmbeddings`, float32 blob plus model name, digest, dimension, input-text hash, `EmbeddedAt`). An hourly `EmbeddingScanJob` queues idempotent embed jobs for new, changed or stale products, and the drain job (now looping in batches, one model request per `Model:BatchSize` products) embeds them. A nightly `CandidateGenerationJob` builds `MatchCandidates` from stored vectors only, so it also runs while the model is down. Nothing is matched or merged yet: that is Phase 3.

**Rules as implemented (all configurable under `Model:Candidates`).** Top-8 neighbours per product from a different chain with cosine >= 0.6, then hard filters: both sizes known means same unit class (mass / volume / count) and within 2%, otherwise the pair is rejected; either size unknown keeps the pair and marks `SizeKnown=false`. Brands: equal or token-subset is Ok; different is rejected; one missing is Ok if the other brand appears in that listing's name, otherwise `Unknown`. Pairs already on the same canonical are skipped. Each candidate stores cosine, `SizeKnown`, `BrandCheck`, model name and digest.

**Migration `AddEmbeddingsAndCandidates`.** Adds two tables (`StoreProductEmbeddings`, `MatchCandidates`) and four indexes, no changes to existing tables. Applied to a throwaway SQLite file only, together with the two earlier migrations. It is applied to the real database at next startup, after the automatic backup.

**Design decisions worth knowing**
- Stored per StoreProduct, not per canonical (reasons under Phase 2 above).
- The index serves vectors of one identity only: the identity of the most recently embedded vector. After a model or digest change the index shrinks to the recomputed vectors and grows as re-embedding proceeds: fewer candidates, never mixed-model ones.
- An empty index leaves existing candidates untouched instead of wiping them.
- The Phase 1 watch-out is addressed: a response the model sends but that is unusable is now recorded as the breaker's last error (visible in the status panel) instead of being invisible.
- Outage handling from Phase 1 applies to embed jobs unchanged: they wait without using up attempts.

**Verified.** `dotnet test Savvori.sln --filter "FullyQualifiedName!~LiveScraperTests"`: 449 passed, 0 failed (LiveScraperTests not run). The new tests cover: pure size and brand rules; scan and drain storing embeddings with full provenance and batching (10 products, batch size 4, 3 requests); idempotent scanning; text change and digest change marking embeddings stale and recomputing; obsolete jobs; model down leaves products saved and jobs pending, then recovers; dead-lettered jobs not resurrected; inactive products skipped; candidate ordering, score and flags; same-chain and low-cosine exclusion; partially embedded catalogue giving fewer correct candidates; other-model vectors never compared; regeneration removing pairs that stopped qualifying; empty index; incremental index refresh and normalisation; top-K selection.

**Not verified**
- No real embeddings or beta data, so the numbers from the prototype (candidate counts, 862 auto-accept pairs, 2,634 borderline) are not reproduced here and this phase does not change the match report: no product is matched yet, so the before/after match report is identical.
- Speed of brute-force search at 18k x 1024 was not measured. It should be tolerable for a nightly job (it runs in parallel), but check the "Candidate generation" log line and duration on the beta.
- Memory of the index (~75 MB of vectors plus overhead) is an estimate.
- The status panel additions were compiled but not viewed in a browser.

**Threshold caveat.** The defaults (0.6 minimum, top-8, 2% size tolerance) came from a small hand-labelled sample and must be re-checked on real review-queue results.

## Phase 3 report

**What it does.** `MatchingService` (nightly, or "Run matching now" on Admin > Match review) evaluates stored candidates in tiers. Tier A (EAN, exact brand+name+size+unit) is unchanged. Tier B: cosine >= 0.90 (both sizes known) or >= 0.95 (a size unknown) and the brand check passed: auto-accept. Tier C: from 0.80 / 0.85 up to the accept threshold, and confident pairs whose brand is unknown: a `Judge` job is queued; only an explicit judge "yes" accepts. A failed, timed-out or malformed judge call is "no decision": the candidate stays pending and the queue retries, and it is never recorded as a "no". Tier D: everything else from `ReviewMinCosine` (0.70) up, plus judge "no"/"unclear" answers and merges blocked by a safety rule, goes to the review queue. Lower pairs stay unqueued proposals.

**Safety rules.** Two canonicals that both have active prices from the same chain, or carry different EANs, are never merged automatically ("probably different packs"); the reviewer sees the warning and must tick a confirmation. A model decision never moves a manually matched product, or any product in a canonical group that contains one. Human decisions mark both products manually matched so no later job changes them. Groups of three or more merge pairwise through the same checks. Shopping list items follow the surviving canonical.

**Reversibility.** Every applied merge is stored in `MatchMerges` with a snapshot of the retired canonical, the moved products (previous canonical, status, method) and the redirected list items. Undo restores all of it and rejects the pair. Every decision stores method (`embedding-cosine`, `embedding-judge`, `manual-review`), cosine, embedding model and digest, judge model and verdict, and timestamp. Rejected and "different variant" pairs are never proposed again, and candidate regeneration never deletes or overwrites decided pairs.

**Dry run.** `Model:Matching:DryRun` defaults to **true**: Tier B and judge "yes" results become suggestions in the review queue and nothing is linked. Turn it off in configuration for the first real run; suggestions that came from the judge are applied without asking the judge again.

**Degraded mode.** With the flag off, or the breaker not closed, a matching run decides nothing and queues nothing; existing matches and categories are untouched.

**Migration `AddMatchDecisions`.** Adds seven nullable/defaulted decision columns (status, method, suggestion, judge verdict, judge model, decided-at, note) and an index to `MatchCandidates` (the Phase 2 table, empty until the first candidate run), plus the new `MatchMerges` table and index. Nothing existing is altered. Applied to a throwaway SQLite file only; the real database gets it at next startup after the automatic backup.

**Verified.** `dotnet test Savvori.sln --filter "FullyQualifiedName!~LiveScraperTests"`: all pass (LiveScraperTests not run). New tests cover the tier thresholds (and that they are configurable), dry run, degraded mode, judge yes/no/unclear, judge failure and recovery, dry-run judge suggestion applied later without re-asking, idempotent runs, stale-digest candidates, same-chain and EAN blocks with human force, manual protection (product and group), transitive groups, shopping-list redirect, full undo, undo refusal, rejected pairs never re-proposed, regeneration keeping decisions, and the review API against real SQLite (listing, accept/force/undo, reject, different-variant, 404/409, run, mapping stats). The review page renders side by side with the warning and confirmation checkbox (against a mock API).

**Before/after match report.** Not measurable here: it needs your real embeddings. Expected shape from your prototype on the full data: 862 auto-accept pairs forming 647 groups over 1,478 products, and 2,634 borderline pairs for the judge or you. Products priced by 2+ chains is now shown on Admin > Mapping and Match review so you can watch the effect; today it is 1 on the beta.

**Not verified.** Nothing was run against a real Ollama or your data; the review page was not viewed in a browser; the size of the nightly matching run on 18k products is untested. Judge "no" answers are shown in the review queue (with the verdict) rather than hidden, because a 7B judge's recall is only about 52%.

**Threshold caveat.** All defaults (0.90 / 0.95 / 0.80 / 0.85 / 0.70) were tuned on a small hand-labelled sample and should be re-checked on review-queue results before switching off the dry run.

## Phase 4 report

**Taxonomy v2.** Proposed as a document only: `docs/TAXONOMY_V2.md` (12 aisles, 87 categories, Bio / sem lactose / sem glúten as tags, and a mapping from each of today's 32 assignable categories, marking which are 1:1 and which split and how). **Nothing has been migrated and no category was changed.** The migration steps (additive columns, `LegacyCategoryId` kept for reversal, dry-run report first) are described in the document and wait for your approval; the "points for you to decide" at its end need your answers first.

**Classifier (implemented, works with the categories that exist today).** k=7, similarity-weighted vote over stored embeddings of categorised products. Confidence >= 0.85 auto-assign, 0.5 to 0.85 review queue, lower uncategorised (all configurable under `Model:Categories`; `MinNeighbourCosine` 0.5 stops weak neighbours from voting). It only considers products with no category, never learns from its own decisions, and skips products whose suggestion you rejected. `Model:Categories:DryRun` defaults to **true**. It decides nothing while the flag is off or the breaker is not closed, so the existing rule-based `CategoryMapper` stays the degraded-mode path and the model step only adds to it.

**Store-category strings.** Raw strings such as Celeiro's `alimentacao` are normalised and decided once as a whole when at least 5 uncategorised products carry them and the k-NN agreement reaches the auto-assign level; that decision is cached (`CategoryStringDecisions`) and reused for later products with the same string, without re-running the vote. A string whose products disagree (as a generic `alimentacao` will) is stored as Mixed and its products are decided one by one. In dry run a whole-string proposal is offered once as "Apply to all".

**Audit and reversal.** Every decision is a `CategorySuggestions` row: method (`embedding-knn`, `string-cache`, `manual-review`), confidence, embedding model and digest, timestamps, previous category. Undo restores the previous category and rejects the suggestion.

**Migration `AddCategorySuggestions`.** Adds two new tables (`CategorySuggestions`, `CategoryStringDecisions`) and their indexes. No existing table is altered.

**Verified.** `dotnet test Savvori.sln --filter "FullyQualifiedName!~LiveScraperTests"`: all pass (LiveScraperTests not run). New tests cover: vote arithmetic, feature off and degraded mode, assignment with provenance, dry run, existing categories untouched, far-away products staying uncategorised, mixed neighbourhoods not auto-assigned, configurable thresholds, not learning from its own decisions, rejected products not re-proposed, accept / undo / refusal after a manual categorisation, whole-string decisions cached and reused, mixed strings decided per product, small strings not decided as a whole, accepting a string, and the review API against real SQLite.

**Not verified.** The 86% / 95.5% / ~98% accuracy figures from the prototype are not reproduced here (no real embeddings or beta data). The classifier has never seen real categories. Speed of the k-NN pass on about 4,000 uncategorised x 14,000 categorised products was not measured. The pages were not viewed in a browser.

**Before / after.** No categories changed yet, so the uncategorised share (shown on Admin > Mapping) is unchanged. Expected after a real run: most of the 24% uncategorised products get a prediction, about three quarters of them at the auto-assign level. That is the prototype's estimate, not a measured result.

**Coverage note.** Two classifier tests (`MixedNeighbourhood_...`, `MixedStoreString_...`) set `MinNeighbourCosine` to 0 to exercise a mixed vote, so those two branches are covered only off-default. Real bge-m3 similarities between related grocery products should sit well above the default 0.5, so the default is probably right, but that is a judgement, not a measurement.

**Boot check (done).** The real WebApi was started on a fresh database with the flag on and an unreachable model: all five migrations applied through the normal startup path, all scheduled jobs registered without errors, the status/matching/categorisation endpoints answered, real connection-refused errors opened the breaker after the configured failures with jobs left pending (none dead-lettered), and the WebApp rendered Admin > Mapping (showing "Degraded mode"), Match review and Category suggestions with HTTP 200 against the live API. Not done: viewing the pages visually, and any run against a real Ollama.

**Threshold caveat.** 0.85 / 0.5 / k=7 were tuned on a small hand-labelled sample and must be re-checked on the review queue before turning the dry run off.

**What to do next (your decisions).**
1. Answer the open points in `docs/TAXONOMY_V2.md` and approve or change the taxonomy; then I implement the additive migration and the v1 to v2 mapping (dry-run report first).
2. Deploy dark (`Model:Enabled=false`), enable it against your Ollama, let embeddings fill, look at the match dry run and the category dry run, and only then switch off `Model:Matching:DryRun` / `Model:Categories:DryRun`.

## Taxonomy v2 migration report

**Decisions received.** Aisles as proposed (Queijos in Charcutaria e Queijos, Ovos in Laticínios e Ovos), all non-food leaves kept, tags bio / sem-lactose / sem-gluten / vegan / sem-acucar.

**Built.** Explicit admin actions (Admin > Taxonomy v2): a dry-run plan per v1 category, apply, revert, tag backfill. Apply seeds 12 aisles and 87 categories as new rows with English slugs (v1 rows untouched; names are localised pt-PT/en via `ProductCategoryTranslations`), relabels products through the approved mapping with deterministic keyword rules (whole-word, accent-insensitive; unmatched splits are left uncategorised for the classifier, never guessed), keeps `Product.LegacyCategoryId`, and adds tags. Once active the category API shows the v2 tree and the scraper translates the rule mapper's v1 slug to v2 by product name. A revert restores product labels and leaves labels you set by hand alone. The classifier no longer excludes taxonomy-made labels from training (only its own).

**Migration `AddTaxonomyV2`.** Adds two nullable columns to `Products` (`LegacyCategoryId`, `CategorySource`) and two tables (`ProductTags`, `TaxonomyMigrations`). Additive. The label migration itself is NOT run by the schema migration: it happens only when you press Apply.

**Verified.** All tests pass (LiveScraperTests not run): mapping completeness (every v1 category mapped, every target exists), about 40 keyword placement cases, tag rules, plan is a dry run, apply/idempotency, revert (including hand decisions and classifier labels), scraper retargeting before and after, and the whole plan/apply/revert cycle through the API on real SQLite. **Live check** on the running app with real Ollama and 24 seeded products: the plan placed 19 of 21 products by rule or 1:1, applied, the category API showed the 12 aisles, revert restored all 21 products and the v1 tree, no errors.

**Findings from the live run (please read).**
- The classifier can only predict categories that already contain products. The new gap categories (pet food, coffee, chocolate, snacks, sun care, ...) start empty, so with few labelled examples it gave two weak suggestions (confidence about 0.5, for example a dog food product suggested as "Aves") and left the rest uncategorised. That is the classifier behaving correctly (it did not auto-assign), but it means the gap aisles will stay empty until they get a few examples.
- Hand-made v1 labels cannot be told from rule-made ones, so rules place every product of a split category.
- After a revert, a label you set by hand on a v2-only category stays but that category is not shown in the (v1) tree.

**Not verified.** No run on your real catalogue: the plan numbers on your 18k products are unknown until you open Admin > Taxonomy v2. The keyword lists were written from general Portuguese grocery vocabulary and will misplace some products; check the plan counts and a sample after applying.

**Gap seeding (added after the live run).** The new v2 categories that did not exist in v1 (pet food, coffee, tea, chocolate, snacks, nuts, spreads, sugar, frozen extras, sun care, cosmetics, kitchenware, stationery and books, baby furniture, plant drinks, beer, wine, spirits, flour and baking, legumes, energy drinks, animal accessories) get their first examples from conservative whole-word name rules that apply only to products that have no category. At apply time they run for never-categorised products and for products a split left uncategorised, marked `taxonomy-seed`; a revert puts them back to uncategorised. After the migration the scraper applies the same rules to new uncategorised products. Ambiguous words are deliberately not enough on their own ("chocolate" alone, "cola", "sem açúcar"). The plan page shows the seed counts per category before you apply.

## Bulk review (added after validating on beta)

On beta's real data the review queues held about 7,750 match suggestions and 2,850 category suggestions. Nobody needs to click through them: most are low-value (the 0.70 to 0.80 band, judge "no"). The workflow is now: spot-check a random sample, apply everything above a threshold as one background run, undo the whole run if it looks wrong. Judge answers are never bulk-applied, and `Model:Matching:AutoApplyJudgeYes` (default false) also keeps a judge "yes" out of automatic application even with dry run off, because the judge said yes to own-brand vs branded pairs. Migration `AddBulkBatches`: one table plus a `BatchId` on `MatchMerges` and `CategorySuggestions`.

## Addendum: variant guard and identical-name tier (measured on beta, 2026-09-21)

**Measurements** (hand-labelled samples, not a benchmark): merges applied at cosine >= 0.90 were about 97-98% right; a
random 45 of the pending 0.85-0.90 pairs were about 60% right, 0.80-0.85 about 35-40%. Almost every error was a
different flavour or variant with an otherwise identical name ("Ananás" vs "Limão", chocolate 70% vs 85%, "light" vs
regular, "com sal" vs "sem sal").

**Change.** `VariantGuard` compares the words two names do not share (brand, size and filler words removed, crude
plural stem). Both sides having words of their own, or either side carrying a variant marker (light, zero, proteína,
infantil, ...), is a conflict. Automatic paths (bulk apply, nightly auto-accept, judge auto-apply) refuse conflicts and
leave the pair in the queue with the reason. Manual accepts are never affected.

Bulk apply gained an optional *identical names* tier (`exactFloor`, off by default): pairs below the cosine threshold
whose names are identical once brand, size and filler words are removed (about 270 pairs between 0.80 and 0.90 on beta,
about 96% right in a 45-pair sample). It is recorded with method `embedding-exact-name`, so it is separable and undoable
like any bulk run.

**What this does not fix.** One-sided extra words that are real variants and not in the marker list ("cálcio",
"baunilha", "cebola e alho") still pass; the unflagged remainder of the 0.85-0.90 band was about 80% right, so that band
is not bulk-applied. The word lists were tuned on this one data set.

## Addendum: the dry-run switch is gone (2026-09-22)

`Model:Matching:DryRun`, `Model:Categories:DryRun` and `Model:Matching:AutoApplyJudgeYes` were removed. Sections above that
describe them are history. The model now only ever suggests:

- The matching run sorts candidates into the review queue (confident suggestions, judge jobs, review band); it never links
  anything. A judge "yes" is a suggestion like any other.
- The classifier writes suggestions only. The one thing that categorises without a fresh click is a whole store category
  you already accepted ("Apply to all"), which then applies to new products carrying the same string.
- Applying is a person's action: accept or reject per item, bulk apply (undoable in one step), or accepting a whole store
  category. Bulk apply is the only automatic-path caller of `MatchApplier` and keeps `guardVariants`.

This keeps the brief's guarantee ("a dry run is used for the first run") by making the model permanently more
conservative than a dry run, instead of a switch someone can turn on. On the beta sample the category classifier was about
28% wrong even at confidence 1.00, which is why it is never given the authority to assign on its own.

## Addendum: raw store category on StoreProduct, and a category judge (2026-09-23)

**`StoreProduct.Category`.** The k-NN classifier only ever saw `"{brand} {name}"` as text, which produces
confident-but-wrong suggestions when a product's name shares a word with an unrelated category (a rice cooker
suggested into "Arroz", "congelado" confused with "Gelados"). The store's own raw category text for that listing
(e.g. "Congelados") is a strong disambiguator but was never actually persisted per listing — a `StoreCategoryId`
navigation looked like it should carry this but was dead code, unpopulated by anything outside migrations. Added a
real `StoreProduct.Category` string, filled from the scraper's own category text on every scrape (create and
update). It is **not** mixed into the shared embedding used for matching: matching's cosine thresholds were
calibrated on name-only text, and each chain names categories differently, so doing that needs its own measured
experiment first, not a hunch. It is available for the categorisation side, which is where it is used below.

**Category judge.** A new `ICategoryJudge` (mirrors `IPairJudge`: same breaker wrapping, same Ollama chat
transport, `qwen2.5:7b-instruct`) checks each category suggestion before `CategoryBulkService.RunApplyAsync`
assigns it — after the deterministic `CategoryGuard` word-list filter (kept as a free first pass; the judge only
runs on what the guard lets through). A verdict of anything other than "yes" holds the suggestion back with a
note instead of assigning it, exactly like a `CategoryGuard` hit; a transport failure or malformed response does
the same (judged per suggestion, not latched for the rest of a batch, so a transient blip only holds back the
items it actually hit).

The prompt is few-shot (12 hand-picked examples spanning literal-word collisions and legitimate variants/formats
that must not be rejected). Measured against real prod `CategorySuggestions` with the exact production request
shape (`/api/chat`, same system/user message split, same one-word parse), held out from the few-shot examples:

- 27/27 (100%) of real suggestions rejected by hand at 100% confidence were correctly flagged "no".
- 39/50 (78%) of a fresh random sample of already-accepted suggestions were correctly confirmed "yes"; several of
  the "misses" turned out to be the judge catching suggestions that were wrong despite being accepted by a human
  reviewer (e.g. "Caviar de Esturjão" suggested into "Queijos"), not the judge being wrong.
- On 50 fresh, unlabeled, high-confidence pending suggestions: 29 "yes" (would auto-apply), 21 "no" (would go to
  manual review). Hand-spot-checking all 29 "yes" verdicts found no apparent false accept. The "no" side leans
  over-cautious on some legitimate cases (children's/parenting books in particular) — safe (extra manual review,
  never a wrong assignment) but leaves some real wins on the table; a future prompt iteration could tighten this
  without touching the "yes" side's precision, which is the one that actually matters for safety.

This is deliberately additive to `CategoryGuard`, not a replacement: the guard is free and catches roughly half of
real bad suggestions (anything with a literal non-food/pet/appliance word) before ever calling the model, so only
the harder, guard-blind cases pay for an Ollama round trip.

**Bug caught by CI before this reached beta.** `CategoryBulkService` initially called the judge unconditionally,
so bulk apply made a live model call even with `Model:Enabled` off — inconsistent with every other model consumer
in the pipeline. The `Testing` environment has no `Model` section (`Enabled` defaults to false), so
`Savvori.Api.Tests`' end-to-end bulk-apply test should never have touched the network at all; it happened to pass
locally because the dev machine has a route to the real Ollama host and got a real "yes", but failed on GitHub's
runner (no route) with `ModelUnavailableException`, correctly held back, and broke the test's old assumption of
guard-only apply. Fixed by gating the judge call behind `options.Value.Enabled`, matching every other consumer.

**Validated on beta (2026-09-23).** Ran a real bulk apply at 0.95 confidence against beta's live queue (16
eligible): 5 applied, 11 held back (1 by `CategoryGuard`, 10 by the judge). All 5 applied suggestions were
correct on inspection. Of the 10 judge holds, about 6 were clear correct catches (two "Bebida Alpro Soja..."
suggested into "Sumos e Néctares", a Twix chocolate snack into "Bolachas Maria e Simples", a coffee drink into
"Leite", a biscuit brand into "Bolachas Maria e Simples" and another into "Manteiga e Margarinas"), and about 4
were the known over-cautious pattern on baby-related products confirmed here for the first time on real
suggestions rather than just spot-checks (two cots/beds and a playpen correctly belonging in "Puericultura e
Mobiliário Bebé", baby wipes correctly belonging in "Fraldas e Higiene Bebé" — all wrongly held back). Batch
undone afterward to leave beta's queue as found. No false accepts observed. Next: promote to prod through the
normal pipeline (DB snapshot first).

**Follow-up: targeted fix for the baby-product/book over-caution (2026-09-23, same day).** Added 5 more few-shot
examples (a children's bed, baby wipes, and three children's/parenting books) plus a guidance sentence calling
out that baby gear and children's books are genuinely their own category even when they read unlike a plain adult
product. Measured on a dedicated held-out sample of 17 real baby/book suggestions pulled from prod (cots, a
playpen, a baby-safety mirror, a toy, and several parenting/children's books): went from 11/17 (65%) before the
fix to 13/15 (87%, after excluding the 2 items now used as few-shot examples) after. Recall on the 27 known-bad
suggestions held at 100% throughout both rounds. The fresh 50-item accepted-suggestions sample shifted a little
in both directions between rounds (39-43 out of 50 depending on the exact prompt) — normal noise from a longer
prompt nudging a handful of unrelated borderline calls, not a regression in the metric that actually matters
(nothing in either round showed a false accept). Diminishing returns are visible at this point: further few-shot
tuning trades a small number of items against each other rather than producing clean wins, so this is a
reasonable stopping point for prompt-only tuning.

## Addendum: pair judge (matching) investigation, same day — no changes shipped

Tried to carry the category judge's win (few-shot prompting) over to `OllamaPairJudge`. Result: **no code change**,
because nothing tested beat the existing bare 3-line prompt. Recorded here so this ground isn't retested blind.

**First measurement was wrong, then corrected.** An initial test harness omitted the `Size` field from the pair
description, unlike production's `Describe(JudgeItem)` which sends `Name | Brand | Size | Chain`. That made the
judge look badly broken (42% recall on 60 real applied matches). Adding `Size` back and re-measuring on the same
data gave the true baseline: **75% recall** (45/60) on real applied matches, **81% precision** (29/36) on real
rejected/different-variant pairs — a solid, working judge, not a broken one. Lesson: always assemble the eval
harness from the exact production request-building code, not a hand-rebuilt approximation of it.

**Few-shot prompting was tried and measurably hurt matching**, unlike categories. Adding 6-7 hand-picked examples
(mirroring the category judge's approach) dropped recall to 60% for a marginal precision gain (81%→83%). Not
shipped. The category judge's problem was a genuinely under-specified prompt; the pair judge's apparent problem
was the missing-Size measurement bug above, not the prompt itself.

**`qwen3-vl:8b` as a stronger judge: ruled out.** It's a thinking model on this Ollama deployment, and neither
`"think": false` at the request root nor a short `num_predict` suppresses its internal reasoning — the `content`
field stays empty until the model finishes thinking (which routinely exceeds 400 tokens on this prompt), so short
`num_predict` truncates before any answer and gets parsed as Unclear. Confirmed the model can eventually produce a
clean one-word `content` (verified on a trivial arithmetic prompt at `num_predict: 300`), but at production prompt
length the 93-pair eval took 4m41s (~3s/call) and still landed almost entirely on Unclear. Not practical as a
per-pair judge without a template/API change on the Ollama side (check `/api/show` for template handling of
`think` before revisiting).

**Unit-price ratio: tested, does not cleanly separate matches from non-matches.** The hypothesis (own-brand vs.
branded pairs should show a large €/kg gap) doesn't hold up on real data: positives (real matches) range up to
2.31x (driven by promo pricing and pack-size/format differences, e.g. "Queijo de Ovelha Seia Amanteigado" at 22.18
vs 9.59), while negatives (real rejects) only reach 1.59x. The distributions overlap heavily (p75 1.33x positives
vs 1.49x negatives; p90 1.47x vs 1.59x). A ratio gate at any reasonable threshold would reject real matches at
roughly the same rate it catches real non-matches. Not built.

**Low-cosine review-queue band (0.70-0.85, ~80% of the 6,961-item NeedsReview backlog, never reaches the judge)
spot-checked by eye, not a hidden recall goldmine.** The pairs here are overwhelmingly genuinely different
products that share a brand or category word (e.g. "Azeite Virgem Extra Clássico" vs "...Seleção Azeitonas
Maduras", "Fermento em Pó" vs "Gelatina em Pó de Morango", both Royal-branded). Sitting unjudged in NeedsReview is
the correct outcome for most of this band, not a bug.

**Net conclusion:** the current `OllamaPairJudge` prompt, as shipped before this investigation, is already solid
(75%/81%) and nothing tested here beat it. No code changes were made to matching as a result of this session.

