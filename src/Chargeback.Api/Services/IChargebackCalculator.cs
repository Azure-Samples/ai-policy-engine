using Chargeback.Api.Models;

namespace Chargeback.Api.Services;

/// <summary>
/// Calculates chargeback costs based on model-specific pricing.
/// </summary>
public interface IChargebackCalculator
{
    decimal CalculateCost(CachedLogData logData);
    decimal CalculateCustomerCost(CachedLogData logData, PlanData plan);
}
