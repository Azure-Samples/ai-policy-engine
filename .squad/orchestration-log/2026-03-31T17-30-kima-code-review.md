# Kima — Code Review Fixes (B2, B3, S5, S6, S8)

**Agent:** Kima (Frontend Dev)  
**Mode:** background  
**Model:** claude-sonnet-4.5  
**Date:** 2026-03-31T17:30:00Z  
**Review Findings Assigned:** B2, B3, S5, S6, S8  

## Outcome: SUCCESS ✅

All 5 findings fixed. `tsc -b` clean. `vite build` clean. No new linting issues.

### Fixes Implemented

1. **B2 — billingPeriod type mismatch**
   - Changed `RequestSummaryResponse.billingPeriod` from `{ year: number; month: number }` to `string` (YYYY-MM format)
   - Updated `RequestBilling.tsx` to display string directly instead of accessing `.month`/`.year`
   - Matches backend serialization

2. **B3 — RouteRule missing fields**
   - Added `priority: number` and `enabled: boolean` to `RouteRule` type
   - Updated `RoutingPolicies.tsx` rule builder:
     - Priority number input (auto-incremented on add)
     - Enabled checkbox (defaults to true)
     - Rule display shows priority badge and enable/disable toggle

3. **S5 — ModelPricing base type consolidation**
   - Added `multiplier: number` and `tierName: string` to base `ModelPricing` type
   - Added fields to `ModelPricingCreateRequest`
   - Eliminated `Extended` type variants:
     - Removed `ModelPricingExtended`
     - Removed `PlanDataExtended`
     - Removed `ClientAssignmentExtended`
   - Folded all extended fields into base types
   - Removed all `as Extended` type assertions (7 component files)

4. **S6 — Plan request type safety**
   - Added `modelRoutingPolicyId`, `monthlyRequestQuota`, `overageRatePerRequest`, `useMultiplierBilling` to `PlanCreateRequest`
   - Added same fields to `PlanUpdateRequest`
   - `Plans.tsx` no longer bypasses type checking via untyped object spread

5. **S8 — Rich API error messages**
   - Added `parseErrorMessage()` helper function to `api.ts`
   - Extracts `error` or `message` field from backend JSON responses
   - Applied to all 24 API functions across endpoints:
     - Plans, Clients, Pricing, Routing Policies, Request Billing, Export endpoints
   - Users now see actionable messages (e.g., "Deployment not allowed") instead of generic "Bad Request"

### Build Results

**TypeScript:** `tsc -b` — clean (no errors, no new warnings)  
**Vite Build:** `vite build` — clean (2556 modules, 11.5s)  
**Linting:** 9 pre-existing errors (inherited from base), 0 new issues  

### Files Modified

- `src/chargeback-ui/src/types.ts` — type definitions
- `src/chargeback-ui/src/api.ts` — API client with error parsing
- `src/chargeback-ui/src/components/Plans.tsx`
- `src/chargeback-ui/src/components/Clients.tsx`
- `src/chargeback-ui/src/components/Pricing.tsx`
- `src/chargeback-ui/src/components/RoutingPolicies.tsx`
- `src/chargeback-ui/src/components/ClientDetail.tsx`
- `src/chargeback-ui/src/components/RequestBilling.tsx`
- `src/chargeback-ui/src/components/Export.tsx`

### Status: READY FOR MERGE
