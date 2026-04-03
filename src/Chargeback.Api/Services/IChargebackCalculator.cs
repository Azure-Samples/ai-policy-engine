using Chargeback.Api.Models;

namespace Chargeback.Api.Services;

/// <summary>
/// Calculates chargeback costs based on model-specific pricing.
/// </summary>
public interface IChargebackCalculator
{
    decimal CalculateCost(CachedLogData logData);
    decimal CalculateCustomerCost(CachedLogData logData, PlanData plan);

    /// <summary>
    /// Returns the effective cost of a single request using the model's multiplier.
    /// Baseline = 1.0. GPT-4.1-mini = 0.33, Premium = 3.0, etc.
    /// Defaults to 1.0 for unknown models or invalid multipliers (≤0).
    /// </summary>
    decimal CalculateEffectiveRequestCost(CachedLogData logData);

    /// <summary>
    /// Calculates overage cost when a request exceeds the plan's monthly request quota.
    /// Returns 0 when multiplier billing is disabled or quota is unlimited.
    /// </summary>
    decimal CalculateMultiplierOverageCost(decimal effectiveCost, decimal currentUsage, PlanData plan);

    /// <summary>
    /// Returns the pricing tier name for a deployment/model (e.g., "Standard", "Premium").
    /// Defaults to "Standard" for unknown deployments.
    /// </summary>
    string GetTierName(string deploymentId, string? model);

    /// <summary>
    /// Returns the raw multiplier value for a deployment/model.
    /// Defaults to 1.0 for unknown deployments or invalid values (≤0).
    /// </summary>
    decimal GetMultiplier(string deploymentId, string? model);
}
