# Non-AI API Limits Test Plan

> Drafted from requirements plus current proposal in `.squad/decisions/inbox/mcnulty-non-ai-api-limits-architecture.md`. This is a scenario list only, not test code. Adjust endpoint names, payload shapes, and observability assertions once Freamon's implementation lands.

## Test Cases

### Plan CRUD — schema validation

### 1. `CreatePlan_WithAiAndNonAiLimits_RoundTripsBothLimits`
- **Setup:** Authenticated admin request; payload includes existing AI fields plus proposed non-AI fields (`NonAiRequestsPerMinute`, `NonAiMonthlyRequestQuota`).
- **Action:** `POST /api/plans`, then `GET /api/plans` or read created plan by returned id.
- **Expected result:** `201 Created`; response includes both AI and non-AI values unchanged; subsequent read returns the same values; no existing AI fields are dropped.
- **Layer:** integration

### 2. `CreatePlan_WithOnlyNonAiLimits_Succeeds`
- **Setup:** Authenticated admin request; payload sets non-AI limits only and leaves AI-specific limits at existing defaults/optional values.
- **Action:** `POST /api/plans`.
- **Expected result:** `201 Created`; non-AI fields persist; AI fields deserialize to current defaults and remain valid for existing AI flows.
- **Layer:** integration

### 3. `CreatePlan_WithOnlyAiLimits_DefaultsNonAiFields`
- **Setup:** Authenticated admin request; payload contains only today's AI fields.
- **Action:** `POST /api/plans` and read the created plan back.
- **Expected result:** `201 Created`; non-AI fields default sensibly. Current McNulty proposal is `0 = unlimited`; confirm that before coding the assertion.
- **Layer:** integration

### 4. `UpdatePlan_AddNonAiLimits_PreservesExistingAiFields`
- **Setup:** Existing plan already has AI quota/rate-limit fields populated; non-AI fields absent/defaulted.
- **Action:** `PUT /api/plans/{planId}` with only non-AI fields populated.
- **Expected result:** `200 OK`; new non-AI values are stored; prior AI values are unchanged; response round-trips the full merged plan.
- **Layer:** integration

### 5. `CreateOrUpdatePlan_NegativeNonAiLimits_Returns400`
- **Setup:** Payload sets `NonAiRequestsPerMinute < 0` and/or `NonAiMonthlyRequestQuota < 0`.
- **Action:** `POST /api/plans` and `PUT /api/plans/{planId}`.
- **Expected result:** `400 BadRequest`; validation error identifies invalid non-AI field(s); no plan is created/updated.
- **Layer:** integration

### 6. `CreateOrUpdatePlan_ZeroNonAiLimits_UsesApprovedZeroSemantics`
- **Setup:** Payload sets one or both non-AI fields to `0`.
- **Action:** Create/update a plan, then exercise the non-AI enforcement path.
- **Expected result:** Architecture question. McNulty's current proposal says `0 = unlimited`; if that is approved, create/update succeeds and enforcement never blocks on that field. If the team instead chooses `0 = blocked`, tests must assert immediate denial.
- **Layer:** integration

### Rate limit enforcement (per minute)

### 7. `NonAiPrecheck_FirstRequestUnderPerMinuteLimit_Returns200`
- **Setup:** Client assigned to plan with `NonAiRequestsPerMinute = N` where `N > 1`; non-AI monthly usage below quota; per-minute counter empty.
- **Action:** Call the proposed non-AI precheck endpoint (currently expected to be `GET /api/precheck-rest/{clientAppId}/{tenantId}`) once.
- **Expected result:** `200 OK`; response shows authorized status and current RPM = 1 (or equivalent observable counter); no monthly quota violation.
- **Layer:** integration

### 8. `NonAiPrecheck_RequestAtPerMinuteBoundary_Returns200`
- **Setup:** Same plan; existing non-AI RPM state already at `N - 1` within the active minute window.
- **Action:** Send the Nth non-AI request in the same minute.
- **Expected result:** `200 OK`; request at the exact boundary is still allowed; counter reflects N.
- **Layer:** integration

### 9. `NonAiPrecheck_RequestBeyondPerMinuteLimit_Returns429`
- **Setup:** Existing non-AI RPM state already at `N` within the active minute window.
- **Action:** Send the (N+1)th non-AI request in the same minute.
- **Expected result:** `429 TooManyRequests`; response/body clearly indicates non-AI per-minute rate limiting; blocked request does not create usable capacity loss beyond the approved ceiling. Exact counter semantics (saturate at N vs increment then reject) must be confirmed because current AI precheck increments before rejecting.
- **Layer:** integration

### 10. `NonAiPrecheck_AfterMinuteWindowExpires_AllowsNextRequest`
- **Setup:** Client previously hit the non-AI per-minute limit in minute window T.
- **Action:** Advance time to T+60s (prefer fake clock/time provider, not real sleeps) and send the next non-AI request.
- **Expected result:** `200 OK`; short-window counter resets for the new minute; response starts the new minute at 1.
- **Layer:** integration

### 11. `NonAiPrecheck_DifferentCustomerKeys_AreRateLimitedIndependently`
- **Setup:** Two customers with different `(clientAppId, tenantId)` pairs share the same plan and same non-AI per-minute limit; customer A is already over limit, customer B is below limit.
- **Action:** Send one request as A and one as B.
- **Expected result:** A gets `429`; B gets `200`; counters/keys remain isolated by the customer key.
- **Layer:** integration

### 12. `NonAiPrecheck_UnsetPerMinuteLimit_DoesNotThrottle`
- **Setup:** Plan leaves `NonAiRequestsPerMinute` null/unset on create or update (or stored as approved default).
- **Action:** Send multiple non-AI requests within one minute.
- **Expected result:** No rate-limit block occurs; monthly quota remains the only non-AI gate. Current proposal maps null to `0 = unlimited`; confirm that before coding.
- **Layer:** integration

### 13. `AiAndNonAiRequests_UseIndependentRateCounters`
- **Setup:** Plan has both AI RPM limits and non-AI RPM limits enabled; customer has active AI traffic and non-AI traffic.
- **Action:** Send AI precheck/log traffic and non-AI precheck/log traffic in alternating order.
- **Expected result:** AI requests only affect AI counters; non-AI requests only affect non-AI counters; exhausting one bucket does not decrement or block the other.
- **Layer:** integration

### Monthly quota enforcement

### 14. `NonAiMonthlyQuota_RequestBelowLimit_Returns200AndIncrementsCounter`
- **Setup:** Plan has `NonAiMonthlyRequestQuota = M`; customer's non-AI monthly count is below M.
- **Action:** Send one allowed non-AI request through precheck + logging path.
- **Expected result:** `200 OK`; durable monthly counter increments by 1; usage surfaced in response/log/dashboard state if exposed.
- **Layer:** integration

### 15. `NonAiMonthlyQuota_RequestAtBoundary_Returns200`
- **Setup:** Customer monthly counter is `M - 1`.
- **Action:** Send one non-AI request.
- **Expected result:** `200 OK`; counter becomes exactly M; next over-limit request is the first rejection.
- **Layer:** integration

### 16. `NonAiMonthlyQuota_RequestBeyondLimit_ReturnsConfiguredRejectionStatus`
- **Setup:** Customer monthly counter is already M.
- **Action:** Send one more non-AI request.
- **Expected result:** Prefer `429 TooManyRequests` for consistency with current AI precheck and McNulty's proposal; flag `403 Forbidden` as an alternative only if the team wants quota exhaustion to mean hard authorization denial.
- **Layer:** integration

### 17. `NonAiMonthlyQuota_CounterPersistsAcrossMinuteWindows`
- **Setup:** Customer has consumed some non-AI monthly requests and the short-window RPM key has expired.
- **Action:** Advance only the minute window and send another non-AI request.
- **Expected result:** Per-minute counter resets, but monthly counter continues accumulating from prior value rather than resetting.
- **Layer:** integration

### 18. `NonAiMonthlyQuota_SameClientDifferentTenant_AreTrackedIndependently`
- **Setup:** Same `clientAppId`, two different `tenantId` values, shared plan, different monthly usage states.
- **Action:** Send one request for each tenant.
- **Expected result:** Each tenant uses its own monthly counter; exhausting tenant A does not block tenant B.
- **Layer:** integration

### 19. `NonAiMonthlyQuota_PeriodRollover_UsesApprovedResetModel`
- **Setup:** Customer is at or over non-AI monthly quota near the end of a billing period.
- **Action:** Advance time past the selected reset boundary and send the next non-AI request.
- **Expected result:** Architecture question. McNulty's proposal uses the existing billing-period calculator (`BillingCycleStartDay` / calendar-style billing period), not APIM's rolling quota window. Confirm reset model before coding.
- **Layer:** integration

### Integration with existing AI flows (regression)

### 20. `Plan_WithNonAiLimits_DoesNotChangeAiQuotaOrRateLimitBehavior`
- **Setup:** Plan has both existing AI limits and new non-AI limits enabled; baseline AI behavior is already covered by current precheck tests.
- **Action:** Re-run existing AI precheck scenarios on the mixed plan.
- **Expected result:** Existing AI status codes and response fields remain unchanged; no regression in token quota, TPM, RPM, routing, or allowed deployment enforcement.
- **Layer:** integration

### 21. `NonAiRequests_SucceedWhenAiQuotaIsExhaustedButNonAiQuotaRemains`
- **Setup:** Same customer is over AI token/request limits but still under non-AI monthly and per-minute limits.
- **Action:** Call AI precheck, then call non-AI precheck.
- **Expected result:** AI request is rejected per existing rules; non-AI request still succeeds because counters are independent.
- **Layer:** integration

### 22. `PlanRepository_NonAiFields_RoundTripThroughCosmos`
- **Setup:** Persist a plan containing non-AI fields through the Cosmos-backed repository/cache path.
- **Action:** Upsert plan, clear Redis cache, then read it back.
- **Expected result:** New non-AI fields survive Cosmos serialization/deserialization and repopulate Redis correctly alongside existing fields.
- **Layer:** integration

### 23. `UpdatePlan_NonAiLimits_RefreshesCachedPlan`
- **Setup:** Seed cached plan in Redis, then update its non-AI fields through plan CRUD.
- **Action:** Call `PUT /api/plans/{planId}`, then fetch the plan again through the repository-backed endpoint.
- **Expected result:** Returned plan reflects updated non-AI values immediately; stale cached values are not served after update.
- **Layer:** integration

### Edge cases & failure modes

### 24. `NonAiLogEndpoint_FireAndForgetFailure_DoesNotBlockPrimaryApiResponse`
- **Setup:** APIM/non-AI pipeline uses fire-and-forget outbound logging (current McNulty proposal: `send-one-way-request` to `POST /api/log-rest`); backend log endpoint is unavailable or times out.
- **Action:** Send an otherwise authorized non-AI API request through the APIM flow.
- **Expected result:** Primary API response is still returned to the caller; failure is logged/metriced for operators; no outage is caused by the logging sidecar path.
- **Layer:** integration

### 25. `NonAiPrecheck_PlanLookupFailure_UsesApprovedFallbackStrategy`
- **Setup:** Simulate Cosmos read failure when resolving the assigned plan, with and without a warm Redis cache entry.
- **Action:** Call the non-AI precheck endpoint.
- **Expected result:** Open architecture question. Preferred test split is: cached plan available -> precheck still succeeds using cache; no cached plan -> explicit fail-closed response (`500` or `503`) with no counter mutation. Final status code needs sign-off.
- **Layer:** integration

### 26. `NonAiPrecheck_ConcurrentRequestsAtBoundary_DoNotDoubleSpendCapacity`
- **Setup:** Plan has a small non-AI RPM limit; counter sits at `N - 1`; fire multiple parallel non-AI precheck requests for the same customer.
- **Action:** Send concurrent requests that compete for the final slot.
- **Expected result:** At most one request consumes the final allowed slot; remaining requests are rejected; no race allows more than N successful requests in the minute. Also verify monthly counters do not over-increment for rejected requests.
- **Layer:** load

### Load test scenarios

### 27. `NonAiPrecheck_Load_1000RpsAcrossTenKeys_EnforcesLimitsWithoutCounterDrift`
- **Setup:** 10 customer keys, each on a plan with `NonAiRequestsPerMinute = 100`; steady 1000 requests/sec for 2 minutes; non-AI monthly quota high enough not to interfere.
- **Action:** Run an NBomber scenario similar to the existing load-test `precheck` scenario, but rotate across 10 customer keys and hit the non-AI precheck path.
- **Expected result:** Approximately `10 keys x 100 requests/min x 2 minutes = 2000` successful responses and the remainder `429`; no counter drift between observed successes and stored counters; p99 latency stays under the team's agreed target; no hotspot causes one key to steal capacity from another.
- **Layer:** load

## Open Questions for Architecture

- Confirm McNulty's proposed schema and contract: flat `PlanData` fields (`NonAiRequestsPerMinute`, `NonAiMonthlyRequestQuota`) plus `ClientPlanAssignment.NonAiCurrentPeriodRequests`, with dedicated `/api/precheck-rest` and `/api/log-rest` endpoints.
- Zero-value semantics: keep `0 = unlimited` (McNulty proposal) or treat `0` as fully blocked?
- Over-limit status code: standardize on `429` (proposal/current AI pattern) or use `403` for monthly quota exhaustion?
- Reset model: shared billing-period reset via `BillingPeriodCalculator`, fixed calendar month, custom billing-cycle day, or rolling 30-day window?
- Counter mutation semantics on rejected requests: should a blocked per-minute request saturate at N or increment past N before rejection? Should rejected monthly-quota requests increment the durable monthly counter?
- Failure policy when plan lookup/storage fails: serve from cache if possible, and if not possible fail closed with `500` or `503`?
- Observability contract for APIM-native or hybrid enforcement: if some limits live in APIM policy, what telemetry or headers make counters/assertions testable?
- Fire-and-forget logging contract: do failed `/api/log-rest` calls only emit warnings/metrics, or should they trigger retries/dead-letter handling?

## Notes on Test Code

These scenarios fit the existing layout and naming already in `src/AIPolicyEngine.Tests/EndpointTests.cs` (`CreatePlan_*`, `UpdatePlan_*`, `Precheck_*`) plus the simulation-style integration tests under `src/AIPolicyEngine.Tests/Integration/` such as `PrecheckRoutingIntegrationTests.cs` and `CosmosPersistenceResilienceTests.cs`. The likely implementation path is: extend `EndpointTests` for plan CRUD and basic non-AI precheck status cases, add a dedicated integration test class for non-AI counter isolation/reset/failure behavior, and extend `src/AIPolicyEngine.LoadTest/Program.cs` with a non-AI precheck scenario. New helpers/fixtures will likely be a seeded non-AI RPM key helper, a way to seed/read `NonAiCurrentPeriodRequests`, and ideally a controllable clock/time provider so minute-window and billing-period rollover tests do not depend on real time.
