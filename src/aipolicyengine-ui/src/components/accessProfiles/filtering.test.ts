import { describe, it, expect } from "vitest"
import {
  cellMatchesSearch,
  cellMatchesOverride,
  selectFilteredView,
  type AccessApiSection,
} from "./filtering"
import type { AccessGridCellData } from "./types"
import type { PlanData } from "../../types"

// Test fixtures
const mockPlanData: Record<string, PlanData> = {
  "plan-1": {
    id: "plan-1",
    name: "Basic Plan",
    monthlyRate: 100,
    monthlyTokenQuota: 1000000,
    tokensPerMinuteLimit: 10000,
    requestsPerMinuteLimit: 100,
    allowOverbilling: false,
    costPerMillionTokens: 0.1,
    rollUpAllDeployments: false,
    deploymentQuotas: {},
    allowedDeployments: ["gpt-4"],
    createdAt: "2024-01-01T00:00:00Z",
    updatedAt: "2024-01-01T00:00:00Z",
  },
  "plan-2": {
    id: "plan-2",
    name: "Premium Plan",
    monthlyRate: 500,
    monthlyTokenQuota: 10000000,
    tokensPerMinuteLimit: 50000,
    requestsPerMinuteLimit: 500,
    allowOverbilling: true,
    costPerMillionTokens: 0.05,
    rollUpAllDeployments: true,
    deploymentQuotas: {},
    allowedDeployments: ["gpt-4", "gpt-3.5"],
    createdAt: "2024-01-01T00:00:00Z",
    updatedAt: "2024-01-01T00:00:00Z",
  },
}

const createMockCell = (overrides: Partial<AccessGridCellData>): AccessGridCellData => ({
  target: {
    kind: "operation",
    apiId: "api-1",
    apiDisplayName: "Test API",
    operationId: "op-1",
    operationDisplayName: "Get Users",
    method: "GET",
    urlTemplate: "/users",
  },
  directProfile: null,
  effective: {
    source: "global",
    sourceLabel: "Global",
    sourceDescription: "From global scope",
    profileId: null,
    planId: "plan-1",
    routingPolicyId: null,
    allowedDeployments: ["gpt-4"],
    enabled: true,
  },
  ...overrides,
})

describe("cellMatchesSearch", () => {
  it("returns true when query is empty", () => {
    const cell = createMockCell({})
    expect(cellMatchesSearch(cell, "", mockPlanData)).toBe(true)
  })

  it("matches apiDisplayName case-insensitively", () => {
    const baseCell = createMockCell({})
    const cell = createMockCell({
      target: { ...baseCell.target, apiDisplayName: "User Management API" },
    })
    expect(cellMatchesSearch(cell, "user management", mockPlanData)).toBe(true)
    expect(cellMatchesSearch(cell, "user", mockPlanData)).toBe(true)
    expect(cellMatchesSearch(cell, "management", mockPlanData)).toBe(true)
  })

  it("matches operationDisplayName case-insensitively", () => {
    const baseCell = createMockCell({})
    const cell = createMockCell({
      target: { ...baseCell.target, operationDisplayName: "Get All Users" },
    })
    expect(cellMatchesSearch(cell, "get all", mockPlanData)).toBe(true)
    expect(cellMatchesSearch(cell, "users", mockPlanData)).toBe(true)
  })

  it("matches method case-insensitively", () => {
    const baseCell = createMockCell({})
    const cell = createMockCell({
      target: { ...baseCell.target, method: "POST" },
    })
    expect(cellMatchesSearch(cell, "post", mockPlanData)).toBe(true)
  })

  it("matches urlTemplate case-insensitively", () => {
    const baseCell = createMockCell({})
    const cell = createMockCell({
      target: { ...baseCell.target, urlTemplate: "/api/v1/users/{id}" },
    })
    expect(cellMatchesSearch(cell, "/api/v1", mockPlanData)).toBe(true)
    expect(cellMatchesSearch(cell, "users", mockPlanData)).toBe(true)
  })

  it("matches plan name from plansById", () => {
    const cell = createMockCell({
      effective: {
        source: "global",
        sourceLabel: "Global",
        sourceDescription: "From global scope",
        profileId: null,
        planId: "plan-2",
        routingPolicyId: null,
        allowedDeployments: ["gpt-4"],
        enabled: true,
      },
    })
    expect(cellMatchesSearch(cell, "premium", mockPlanData)).toBe(true)
  })

  it("handles null planName when effective is missing", () => {
    const cell = createMockCell({ effective: null })
    expect(() => cellMatchesSearch(cell, "test", mockPlanData)).not.toThrow()
    expect(cellMatchesSearch(cell, "test api", mockPlanData)).toBe(true) // Still matches other fields
  })

  it("handles planId not found in plansById (falls back to planId)", () => {
    const cell = createMockCell({
      effective: {
        source: "global",
        sourceLabel: "Global",
        sourceDescription: "From global scope",
        profileId: null,
        planId: "unknown-plan",
        routingPolicyId: null,
        allowedDeployments: [],
        enabled: true,
      },
    })
    expect(cellMatchesSearch(cell, "unknown-plan", mockPlanData)).toBe(true)
  })

  it("returns false when no fields match", () => {
    const cell = createMockCell({})
    expect(cellMatchesSearch(cell, "nonexistent", mockPlanData)).toBe(false)
  })

  it("matches apiId case-insensitively", () => {
    const baseCell = createMockCell({})
    const cell = createMockCell({
      target: { ...baseCell.target, apiId: "user-api-v2" },
    })
    expect(cellMatchesSearch(cell, "user-api", mockPlanData)).toBe(true)
    expect(cellMatchesSearch(cell, "v2", mockPlanData)).toBe(true)
  })

  it("matches operationId case-insensitively", () => {
    const baseCell = createMockCell({})
    const cell = createMockCell({
      target: { ...baseCell.target, operationId: "GetUserById" },
    })
    expect(cellMatchesSearch(cell, "getuserbyid", mockPlanData)).toBe(true)
    expect(cellMatchesSearch(cell, "byid", mockPlanData)).toBe(true)
  })
})

describe("cellMatchesOverride", () => {
  it("returns true for 'all' filter regardless of directProfile", () => {
    const cellWithOverride = createMockCell({
      directProfile: {
        id: "profile-1",
        partitionKey: "pk1",
        clientAppId: "client-1",
        tenantId: "tenant-1",
        apiId: "api-1",
        operationId: "op-1",
        planId: "plan-1",
        routingPolicyId: null,
        allowedDeployments: ["gpt-4"],
        enabled: true,
        createdBy: "user1",
        createdAt: "2024-01-01",
        updatedAt: "2024-01-01",
      },
    })
    const cellWithoutOverride = createMockCell({})

    expect(cellMatchesOverride(cellWithOverride, "all")).toBe(true)
    expect(cellMatchesOverride(cellWithoutOverride, "all")).toBe(true)
  })

  it("returns true for 'overrides' filter only when directProfile exists", () => {
    const cellWithOverride = createMockCell({
      directProfile: {
        id: "profile-1",
        partitionKey: "pk1",
        clientAppId: "client-1",
        tenantId: "tenant-1",
        apiId: "api-1",
        operationId: "op-1",
        planId: "plan-1",
        routingPolicyId: null,
        allowedDeployments: ["gpt-4"],
        enabled: true,
        createdBy: "user1",
        createdAt: "2024-01-01",
        updatedAt: "2024-01-01",
      },
    })
    const cellWithoutOverride = createMockCell({})

    expect(cellMatchesOverride(cellWithOverride, "overrides")).toBe(true)
    expect(cellMatchesOverride(cellWithoutOverride, "overrides")).toBe(false)
  })

  it("returns true for 'inherited' filter only when directProfile is absent", () => {
    const cellWithOverride = createMockCell({
      directProfile: {
        id: "profile-1",
        partitionKey: "pk1",
        clientAppId: "client-1",
        tenantId: "tenant-1",
        apiId: "api-1",
        operationId: "op-1",
        planId: "plan-1",
        routingPolicyId: null,
        allowedDeployments: ["gpt-4"],
        enabled: true,
        createdBy: "user1",
        createdAt: "2024-01-01",
        updatedAt: "2024-01-01",
      },
    })
    const cellWithoutOverride = createMockCell({})

    expect(cellMatchesOverride(cellWithOverride, "inherited")).toBe(false)
    expect(cellMatchesOverride(cellWithoutOverride, "inherited")).toBe(true)
  })
})

describe("selectFilteredView", () => {
  const createMockSection = (overrides: Partial<AccessApiSection>): AccessApiSection => ({
    api: {
      id: "api-1",
      displayName: "Test API",
      path: "/test",
      serviceUrl: "https://test.api",
      isCurrent: false,
    },
    apiCell: createMockCell({
      target: {
        kind: "api",
        apiId: "api-1",
        apiDisplayName: "Test API",
        operationId: null,
        method: undefined,
        urlTemplate: undefined,
      },
    }),
    operationCells: [],
    directOverrideCount: 0,
    expanded: false,
    loadingOperations: false,
    operationError: null,
    ...overrides,
  })

  describe("global scope visibility", () => {
    it("shows global cell when it matches search and override filter", () => {
      const globalCell = createMockCell({
        target: {
          kind: "global",
          apiId: "",
          apiDisplayName: "Global Scope",
          operationId: null,
        },
      })
      const sections: AccessApiSection[] = []

      const result = selectFilteredView(sections, globalCell, "", "all", mockPlanData)
      expect(result.globalVisible).toBe(true)
      expect(result.visibleScopeCount).toBe(1)
    })

    it("hides global cell when it does not match search", () => {
      const globalCell = createMockCell({
        target: {
          kind: "global",
          apiId: "",
          apiDisplayName: "Global Scope",
          operationId: null,
        },
      })
      const sections: AccessApiSection[] = []

      const result = selectFilteredView(sections, globalCell, "nonexistent", "all", mockPlanData)
      expect(result.globalVisible).toBe(false)
      expect(result.visibleScopeCount).toBe(0)
    })

    it("hides global cell when it does not match override filter", () => {
      const globalCell = createMockCell({
        target: {
          kind: "global",
          apiId: "",
          apiDisplayName: "Global Scope",
          operationId: null,
        },
        directProfile: null,
      })
      const sections: AccessApiSection[] = []

      const result = selectFilteredView(sections, globalCell, "", "overrides", mockPlanData)
      expect(result.globalVisible).toBe(false)
    })
  })

  describe("section visibility under 'all' filter", () => {
    it("shows section when API cell matches", () => {
      const section = createMockSection({
        apiCell: createMockCell({
          target: {
            kind: "api",
            apiId: "api-1",
            apiDisplayName: "User API",
            operationId: null,
          },
        }),
      })

      const result = selectFilteredView([section], null, "user", "all", mockPlanData)
      expect(result.filteredSections).toHaveLength(1)
      expect(result.filteredSections[0].apiCellVisible).toBe(true)
    })

    it("shows section when operation cells match", () => {
      const section = createMockSection({
        operationCells: [
          createMockCell({
            target: {
              kind: "operation",
              apiId: "api-1",
              apiDisplayName: "Test API",
              operationId: "op-1",
              operationDisplayName: "Get Users",
              method: "GET",
              urlTemplate: "/users",
            },
          }),
        ],
      })

      const result = selectFilteredView([section], null, "get users", "all", mockPlanData)
      expect(result.filteredSections).toHaveLength(1)
      expect(result.filteredSections[0].visibleOperationCells).toHaveLength(1)
    })

    it("shows section when API text matches (even if no cells match)", () => {
      const section = createMockSection({
        api: {
          id: "api-1",
          displayName: "User Management",
          path: "/users",
          serviceUrl: "https://test.api",
          isCurrent: false,
        },
        apiCell: createMockCell({
          target: {
            kind: "api",
            apiId: "api-1",
            apiDisplayName: "Different Name",
            operationId: null,
          },
        }),
      })

      const result = selectFilteredView([section], null, "user management", "all", mockPlanData)
      expect(result.filteredSections).toHaveLength(1)
      expect(result.filteredSections[0].apiTextMatch).toBe(true)
    })

    it("hides section when nothing matches", () => {
      const section = createMockSection({})

      const result = selectFilteredView([section], null, "nonexistent", "all", mockPlanData)
      expect(result.filteredSections).toHaveLength(0)
    })
  })

  describe("section visibility under 'overrides' filter", () => {
    it("hides section when directOverrideCount is 0", () => {
      const section = createMockSection({
        directOverrideCount: 0,
      })

      const result = selectFilteredView([section], null, "", "overrides", mockPlanData)
      expect(result.filteredSections).toHaveLength(0)
    })

    it("shows section with directOverrideCount > 0 even if collapsed (operation overrides)", () => {
      const section = createMockSection({
        directOverrideCount: 2,
        expanded: false,
        apiCell: createMockCell({ directProfile: null }),
        operationCells: [], // Not loaded yet (collapsed)
      })

      const result = selectFilteredView([section], null, "", "overrides", mockPlanData)
      expect(result.filteredSections).toHaveLength(1)
      expect(result.filteredSections[0].apiCellVisible).toBe(false)
      expect(result.filteredSections[0].apiTextMatch).toBe(true) // Empty query matches all
    })

    it("shows section when API cell has directProfile", () => {
      const section = createMockSection({
        directOverrideCount: 1,
        apiCell: createMockCell({
          directProfile: {
            id: "profile-1",
            partitionKey: "pk1",
            clientAppId: "client-1",
            tenantId: "tenant-1",
            apiId: "api-1",
            operationId: null,
            planId: "plan-1",
            routingPolicyId: null,
            allowedDeployments: ["gpt-4"],
            enabled: true,
            createdBy: "user1",
            createdAt: "2024-01-01",
            updatedAt: "2024-01-01",
          },
        }),
      })

      const result = selectFilteredView([section], null, "", "overrides", mockPlanData)
      expect(result.filteredSections).toHaveLength(1)
      expect(result.filteredSections[0].apiCellVisible).toBe(true)
    })
  })

  describe("section visibility under 'inherited' filter", () => {
    it("shows section when API cell has no directProfile and API text matches", () => {
      const section = createMockSection({
        api: {
          id: "api-1",
          displayName: "User API",
          path: "/users",
          serviceUrl: "https://test.api",
          isCurrent: false,
        },
        apiCell: createMockCell({ directProfile: null }),
      })

      const result = selectFilteredView([section], null, "user", "inherited", mockPlanData)
      expect(result.filteredSections).toHaveLength(1)
    })

    it("hides section when API cell has directProfile and API text matches", () => {
      const section = createMockSection({
        api: {
          id: "api-1",
          displayName: "User API",
          path: "/users",
          serviceUrl: "https://test.api",
          isCurrent: false,
        },
        apiCell: createMockCell({
          directProfile: {
            id: "profile-1",
            partitionKey: "pk1",
            clientAppId: "client-1",
            tenantId: "tenant-1",
            apiId: "api-1",
            operationId: null,
            planId: "plan-1",
            routingPolicyId: null,
            allowedDeployments: ["gpt-4"],
            enabled: true,
            createdBy: "user1",
            createdAt: "2024-01-01",
            updatedAt: "2024-01-01",
          },
        }),
      })

      const result = selectFilteredView([section], null, "user", "inherited", mockPlanData)
      expect(result.filteredSections).toHaveLength(0)
    })

    it("shows section when operation cells match (no directProfile)", () => {
      const section = createMockSection({
        operationCells: [
          createMockCell({
            directProfile: null,
            target: {
              kind: "operation",
              apiId: "api-1",
              apiDisplayName: "Test API",
              operationId: "op-1",
              operationDisplayName: "Get Users",
              method: "GET",
              urlTemplate: "/users",
            },
          }),
        ],
      })

      const result = selectFilteredView([section], null, "", "inherited", mockPlanData)
      expect(result.filteredSections).toHaveLength(1)
      expect(result.filteredSections[0].visibleOperationCells).toHaveLength(1)
    })
  })

  describe("visibleScopeCount calculation", () => {
    it("counts correctly when filters are active", () => {
      const globalCell = createMockCell({
        target: { kind: "global", apiId: "", apiDisplayName: "Global", operationId: null },
      })
      const baseCell = createMockCell({})
      const section = createMockSection({
        apiCell: createMockCell({
          target: { kind: "api", apiId: "api-1", apiDisplayName: "API", operationId: null },
        }),
        operationCells: [
          createMockCell({ target: { ...baseCell.target, operationDisplayName: "Op1" } }),
          createMockCell({ target: { ...baseCell.target, operationDisplayName: "Op2" } }),
        ],
      })

      const result = selectFilteredView([section], globalCell, "", "all", mockPlanData)
      expect(result.filtersActive).toBe(false) // No filters active (empty query, "all" filter)
      expect(result.visibleScopeCount).toBe(2) // 1 global + 1 API (not expanded)

      const resultWithSearch = selectFilteredView([section], globalCell, "op", "all", mockPlanData)
      expect(resultWithSearch.filtersActive).toBe(true)
      expect(resultWithSearch.visibleScopeCount).toBe(2) // 2 matching operations
    })

    it("counts correctly when filters are inactive and section is expanded", () => {
      const section = createMockSection({
        expanded: true,
        operationCells: [
          createMockCell({ target: { ...createMockCell({}).target, operationDisplayName: "Op1" } }),
          createMockCell({ target: { ...createMockCell({}).target, operationDisplayName: "Op2" } }),
          createMockCell({ target: { ...createMockCell({}).target, operationDisplayName: "Op3" } }),
        ],
      })

      const result = selectFilteredView([section], null, "", "all", mockPlanData)
      expect(result.filtersActive).toBe(false)
      expect(result.visibleScopeCount).toBe(4) // 1 API + 3 operations (expanded)
    })

    it("counts correctly when filters are inactive and section is collapsed", () => {
      const section = createMockSection({
        expanded: false,
        operationCells: [
          createMockCell({ target: { ...createMockCell({}).target, operationDisplayName: "Op1" } }),
          createMockCell({ target: { ...createMockCell({}).target, operationDisplayName: "Op2" } }),
        ],
      })

      const result = selectFilteredView([section], null, "", "all", mockPlanData)
      expect(result.filtersActive).toBe(false)
      expect(result.visibleScopeCount).toBe(1) // 1 API only (collapsed)
    })
  })

  describe("empty result case", () => {
    it("returns empty filtered sections when nothing matches", () => {
      const section = createMockSection({})

      const result = selectFilteredView([section], null, "nonexistent", "all", mockPlanData)
      expect(result.filteredSections).toHaveLength(0)
      expect(result.globalVisible).toBe(false)
      expect(result.visibleScopeCount).toBe(0)
    })
  })

  describe("filtersActive flag", () => {
    it("sets filtersActive to false when query is empty and filter is 'all'", () => {
      const result = selectFilteredView([], null, "", "all", mockPlanData)
      expect(result.filtersActive).toBe(false)
    })

    it("sets filtersActive to true when query is not empty", () => {
      const result = selectFilteredView([], null, "test", "all", mockPlanData)
      expect(result.filtersActive).toBe(true)
    })

    it("sets filtersActive to true when override filter is not 'all'", () => {
      const result = selectFilteredView([], null, "", "overrides", mockPlanData)
      expect(result.filtersActive).toBe(true)
    })

    it("normalizes query by trimming whitespace", () => {
      const section = createMockSection({
        apiCell: createMockCell({
          target: { kind: "api", apiId: "api-1", apiDisplayName: "Test API", operationId: null },
        }),
      })

      const result = selectFilteredView([section], null, "  test  ", "all", mockPlanData)
      expect(result.filtersActive).toBe(true)
      expect(result.filteredSections).toHaveLength(1)
    })
  })
})
