import { useCallback, useEffect, useRef, useState } from "react"
import { fetchApimApis, fetchApimOperations } from "../api/apim"
import type { HttpError, ApimApiSummary, ApimOperationSummary } from "../types/apim"

interface UseApimCatalogOptions {
  enabled?: boolean
}

interface UseApimCatalogResult {
  apis: ApimApiSummary[]
  catalogLoading: boolean
  catalogError: string | null
  accessDenied: boolean
  operationsByApi: Record<string, ApimOperationSummary[]>
  operationErrors: Record<string, string | null>
  loadingOperationApiIds: string[]
  refreshApis: () => Promise<void>
  ensureOperationsLoaded: (api: ApimApiSummary) => Promise<void>
  refreshOperations: (api: ApimApiSummary) => Promise<void>
}

function getStatus(error: unknown): number | undefined {
  return typeof error === "object" && error !== null && "status" in error
    ? (error as HttpError).status
    : undefined
}

function getErrorMessage(error: unknown, fallback: string): string {
  return error instanceof Error ? error.message : fallback
}

export function useApimCatalog({ enabled = true }: UseApimCatalogOptions = {}): UseApimCatalogResult {
  const [apis, setApis] = useState<ApimApiSummary[]>([])
  const [catalogLoading, setCatalogLoading] = useState(true)
  const [catalogError, setCatalogError] = useState<string | null>(null)
  const [accessDenied, setAccessDenied] = useState(false)
  const [operationsByApi, setOperationsByApi] = useState<Record<string, ApimOperationSummary[]>>({})
  const [operationErrors, setOperationErrors] = useState<Record<string, string | null>>({})
  const [loadingOperationApiIds, setLoadingOperationApiIds] = useState<string[]>([])

  const operationsByApiRef = useRef<Record<string, ApimOperationSummary[]>>({})
  const loadingOperationApiIdsRef = useRef<string[]>([])

  useEffect(() => {
    operationsByApiRef.current = operationsByApi
  }, [operationsByApi])

  useEffect(() => {
    loadingOperationApiIdsRef.current = loadingOperationApiIds
  }, [loadingOperationApiIds])

  const refreshApis = useCallback(async () => {
    if (!enabled) {
      setCatalogLoading(false)
      return
    }

    setCatalogLoading(true)
    setCatalogError(null)

    try {
      const response = await fetchApimApis()
      const nextApis = response.apis ?? []
      const validApiIds = new Set(nextApis.map((api) => api.id))

      setApis(nextApis)
      setAccessDenied(false)
      setOperationsByApi((current) => Object.fromEntries(
        Object.entries(current).filter(([apiId]) => validApiIds.has(apiId)),
      ))
      setOperationErrors((current) => Object.fromEntries(
        Object.entries(current).filter(([apiId]) => validApiIds.has(apiId)),
      ))
    } catch (error) {
      const status = getStatus(error)
      if (status === 401 || status === 403) {
        setAccessDenied(true)
        setApis([])
        return
      }

      setCatalogError(getErrorMessage(error, "Failed to load APIs"))
    } finally {
      setCatalogLoading(false)
    }
  }, [enabled])

  const loadOperations = useCallback(async (api: ApimApiSummary, force = false) => {
    if (!force) {
      if (loadingOperationApiIdsRef.current.includes(api.id)) return
      if (operationsByApiRef.current[api.id]) return
    }

    setLoadingOperationApiIds((current) => current.includes(api.id) ? current : [...current, api.id])
    setOperationErrors((current) => ({ ...current, [api.id]: null }))

    try {
      const response = await fetchApimOperations(api.id)
      setAccessDenied(false)
      setOperationsByApi((current) => ({ ...current, [api.id]: response.operations ?? [] }))
    } catch (error) {
      const status = getStatus(error)
      if (status === 401 || status === 403) {
        setAccessDenied(true)
        return
      }

      setOperationErrors((current) => ({
        ...current,
        [api.id]: getErrorMessage(error, `Failed to load operations for ${api.displayName}`),
      }))
    } finally {
      setLoadingOperationApiIds((current) => current.filter((apiId) => apiId !== api.id))
    }
  }, [])

  useEffect(() => {
    if (!enabled) {
      setCatalogLoading(false)
      return
    }

    void refreshApis()
  }, [enabled, refreshApis])

  return {
    apis,
    catalogLoading,
    catalogError,
    accessDenied,
    operationsByApi,
    operationErrors,
    loadingOperationApiIds,
    refreshApis,
    ensureOperationsLoaded: async (api) => {
      await loadOperations(api)
    },
    refreshOperations: async (api) => {
      await loadOperations(api, true)
    },
  }
}
