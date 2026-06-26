import type { PlanData } from "../../types"
import type { AccessGridCellData } from "./types"

export type OverrideFilter = "all" | "overrides" | "inherited"

export const OVERRIDE_FILTERS: Array<{ value: OverrideFilter; label: string }> = [
  { value: "all", label: "All scopes" },
  { value: "overrides", label: "Direct overrides" },
  { value: "inherited", label: "Inherited only" },
]

export interface AccessApiSection {
  api: { id: string; displayName: string; path: string; serviceUrl: string; isCurrent: boolean }
  apiCell: AccessGridCellData
  operationCells: AccessGridCellData[]
  directOverrideCount: number
  expanded: boolean
  loadingOperations: boolean
  operationError: string | null
}

export interface FilteredSection {
  section: AccessApiSection
  visibleOperationCells: AccessGridCellData[]
  apiCellVisible: boolean
  apiTextMatch: boolean
}

export interface FilteredView {
  filteredSections: FilteredSection[]
  globalVisible: boolean
  visibleScopeCount: number
  filtersActive: boolean
}

/**
 * Check if a cell matches the search query.
 * Pure function: no side effects, depends only on inputs.
 */
export function cellMatchesSearch(
  cell: AccessGridCellData,
  normalizedQuery: string,
  plansById: Record<string, PlanData>,
): boolean {
  if (!normalizedQuery) return true

  const { target, effective } = cell
  const planName = effective ? (plansById[effective.planId]?.name ?? effective.planId) : null
  const haystacks = [
    target.apiDisplayName,
    target.apiId,
    target.operationDisplayName,
    target.operationId,
    target.method,
    target.urlTemplate,
    planName,
  ]

  return haystacks.some((value) => value?.toLowerCase().includes(normalizedQuery))
}

/**
 * Check if a cell matches the override filter.
 * Pure function: no side effects, depends only on inputs.
 */
export function cellMatchesOverride(cell: AccessGridCellData, filter: OverrideFilter): boolean {
  if (filter === "all") return true
  if (filter === "overrides") return Boolean(cell.directProfile)
  return !cell.directProfile
}

/**
 * Compute the complete filtered view of sections and global scope.
 * Pure function: no side effects, depends only on inputs.
 * 
 * @param sections - All API sections (unfiltered)
 * @param globalCell - The client-global scope cell
 * @param searchQuery - User search query (will be normalized)
 * @param overrideFilter - Override filter mode
 * @param plansById - Plan lookup map for search matching
 * @returns Filtered sections, global visibility, scope count, and active filter flag
 */
export function selectFilteredView(
  sections: AccessApiSection[],
  globalCell: AccessGridCellData | null,
  searchQuery: string,
  overrideFilter: OverrideFilter,
  plansById: Record<string, PlanData>,
): FilteredView {
  const normalizedQuery = searchQuery.trim().toLowerCase()
  const filtersActive = normalizedQuery.length > 0 || overrideFilter !== "all"

  const globalVisible = globalCell
    ? cellMatchesSearch(globalCell, normalizedQuery, plansById) && cellMatchesOverride(globalCell, overrideFilter)
    : false

  const filteredSections = sections
    .map((section) => {
      const visibleOperationCells = section.operationCells.filter(
        (cell) => cellMatchesSearch(cell, normalizedQuery, plansById) && cellMatchesOverride(cell, overrideFilter),
      )
      const apiCellVisible =
        cellMatchesSearch(section.apiCell, normalizedQuery, plansById) && cellMatchesOverride(section.apiCell, overrideFilter)
      const apiTextMatch =
        !normalizedQuery ||
        [section.api.displayName, section.api.path].some((value) => value?.toLowerCase().includes(normalizedQuery))

      return { section, visibleOperationCells, apiCellVisible, apiTextMatch }
    })
    .filter(({ section, visibleOperationCells, apiCellVisible, apiTextMatch }) => {
      if (overrideFilter === "overrides") {
        if (section.directOverrideCount === 0) return false
        return apiCellVisible || visibleOperationCells.length > 0 || apiTextMatch
      }

      if (overrideFilter === "inherited") {
        return apiCellVisible || visibleOperationCells.length > 0 || (apiTextMatch && !section.apiCell.directProfile)
      }

      return apiCellVisible || visibleOperationCells.length > 0 || apiTextMatch
    })

  let visibleScopeCount = globalVisible ? 1 : 0
  for (const { section, visibleOperationCells, apiCellVisible } of filteredSections) {
    if (filtersActive) {
      if (apiCellVisible) visibleScopeCount += 1
      visibleScopeCount += visibleOperationCells.length
    } else {
      visibleScopeCount += 1 + (section.expanded ? section.operationCells.length : 0)
    }
  }

  return {
    filteredSections,
    globalVisible,
    visibleScopeCount,
    filtersActive,
  }
}
