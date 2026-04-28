---
name: "additive-feature-extension"
description: "How to extend the AI Policy Engine with new features without breaking existing behavior"
domain: "api-design, architecture"
confidence: "high"
source: "earned — from model routing + multiplier pricing architecture design"
---

## Context
When adding new capabilities (routing, pricing tiers, new enforcement rules), the system must remain backward-compatible. Existing plans, clients, and audit data cannot break.

## Patterns
1. **Default to off:** New feature fields default to null/false/zero — `UseMultiplierBilling = false`, `ModelRoutingPolicyId = null`
2. **Nullable Cosmos extensions:** New fields on `AuditLogDocument` and `BillingSummaryDocument` must be nullable — existing documents won't have them
3. **Precheck is the choke point:** All enforcement converges at `/api/precheck`. Enrich its response rather than adding new enforcement endpoints
4. **Redis-backed config, Cosmos-backed audit:** Configuration entities (plans, policies, pricing) live in Redis. Audit trail lives in CosmosDB. Don't mix.
5. **In-memory cache for hot-path data:** Use the `ChargebackCalculator` pattern (30s refresh, non-blocking) for any data accessed per-request
6. **APIM policy reads precheck response:** The policy trusts precheck. Add new response fields; don't add new APIM-to-backend calls
7. **Frontend types mirror backend DTOs:** `src/chargeback-ui/src/types.ts` must stay in sync with `src/Chargeback.Api/Models/`

## Examples
- Extending `ModelPricing` with `Multiplier` — existing pricing with `Multiplier = 1.0` behaves identically
- Extending precheck with `routedDeployment` — APIM policies that don't read it work fine
- `PlanData.UseMultiplierBilling = false` means legacy billing path is unchanged

## Anti-Patterns
- Adding required fields to existing Cosmos documents (breaks deserialization of old data)
- Creating new Redis data structures when extending existing ones works
- Putting enforcement logic in APIM policy XML instead of in the precheck endpoint (harder to test, harder to debug)
- Breaking the existing API contract (existing frontend must work without changes until updated)
