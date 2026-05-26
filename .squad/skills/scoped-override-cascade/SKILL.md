# Scoped Override Cascade

**Confidence:** medium  
**Validated:** AAA Access Profile architecture independently reviewed and approved by Zack (2026-05-21)

## When to Use

When adding **per-scope overrides** on top of a **global default** setting. Examples:
- Per-client-per-endpoint Plan/Routing overrides (Access Profiles)
- Per-API pricing overrides on top of global pricing
- Per-operation DLP policy overrides on top of API-level settings

## Pattern

### Data Model
- **Composite ID:** `{prefix}:{entity}:{scope1}:{scope2}:{scopeN|_all}`
- **Single partition key:** All override docs in one logical partition for admin queries
- **Deterministic IDs:** Enable point-reads (fastest Cosmos operation) at each cascade level

### Resolution Algorithm
1. Define an ordered list of scopes from most-specific to least-specific
2. For each level, attempt a point-read by composite ID
3. **First match wins** — no merging, no inheritance between levels
4. Final fallback = existing global default (backward-compatible)

### Key Principles
- **No merging:** A match at level N completely determines the result. Don't partially inherit from level N+1.
- **Backward-compatible:** If the scoping context is absent (e.g., query params not passed), the resolver skips all levels and falls through to the existing global behavior.
- **Cacheable:** Deterministic ID → Redis/in-memory cache by exact key, 30s TTL.
- **Auditable:** Store `sourceProfileId` in resolution result so logs show which override was used.

### Cosmos Implementation

```csharp
public interface IScopedOverrideResolver<TResult>
{
    Task<TResult?> ResolveAsync(params string[] scopeValues);
}
```

Resolution attempts point-reads in order:
```
{prefix}:{entity}:{scope1}:{scope2}    → most specific
{prefix}:{entity}:{scope1}:_all        → scope1 only
{prefix}:{entity}:_global:_all         → entity-wide default
null                                    → fall through to legacy
```

### Anti-Patterns
- ❌ Merging fields from multiple levels (complex, hard to debug)
- ❌ Regex or glob matching in scope values (unpredictable, uncacheable)
- ❌ Implicit defaults (always require explicit creation of override docs)
- ❌ Dynamic evaluation order (levels are fixed at design time)

## Examples in This Codebase

- `AccessProfile` (access-profile partition): resolves Plan+Routing per `(client, api, operation)`
- Future: per-API pricing tiers, per-operation content policies

## Related Skills

- `cosmos-repository-pattern` — base repository class used for storage
- `additive-feature-extension` — how to add features without breaking existing behavior
