# Session Log: Architecture Revision — 2026-03-31T15:55:00Z

## Agent

McNulty (Lead/Architect)

## Outcome

✅ SUCCESS — Architecture proposal v2 written.

## Design Decisions Driving Revision

| Decision | Impact |
|----------|--------|
| **CosmosDB source of truth; Redis cache only** | Fundamental storage architecture change. Plans, clients, pricing moved to persistent CosmosDB with write-through caching. Largest body of work. |
| **Per-REQUEST multiplier (not per-token)** | Simpler billing model: `effective_cost = 1 × model_multiplier` per request. Monthly limits are request-based quotas. |
| **Foundry deployment discovery (no pattern matching)** | Routing maps accounts to known deployments from Foundry endpoint using existing `DeploymentDiscoveryService`. No globs or regex. |
| **Rate limits on routed deployment** | RPM/TPM enforcement applies to the routed deployment (backend), not the originally requested model. |

## Decision Inbox

Proposal file staged in `.squad/decisions/inbox/mcnulty-architecture-v2.md`. **Not merged yet** — awaiting user approval.

## Inbox Contents

- `mcnulty-architecture-v2.md` — full architecture proposal with three bodies of work (storage migration, model routing, multiplier pricing)
- `mcnulty-model-routing-pricing-architecture.md` — v1 proposal (superseded)

---

**Logged by:** Scribe  
**Timestamp:** 2026-03-31T15:55:00Z
