using System.Diagnostics.Metrics;
using AIPolicyEngine.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace AIPolicyEngine.Tests;

public class ChargebackMetricsTests
{
    private IMeterFactory CreateMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IMeterFactory>();
    }

    [Fact]
    public void RecordTokensProcessed_DoesNotThrow()
    {
        var meterFactory = CreateMeterFactory();
        var metrics = new ChargebackMetrics(meterFactory);

        metrics.RecordTokensProcessed(100, "tenant", "app-1", "gpt-4o", "gpt-4o");
    }

    [Fact]
    public void RecordRequest_DoesNotThrow()
    {
        var meterFactory = CreateMeterFactory();
        var metrics = new ChargebackMetrics(meterFactory);

        metrics.RecordRequest("tenant", "app", "gpt-4o");
    }

    [Fact]
    public void RecordCost_DoesNotThrow()
    {
        var meterFactory = CreateMeterFactory();
        var metrics = new ChargebackMetrics(meterFactory);

        metrics.RecordCost(0.05, "tenant", "app-1", "gpt-4o");
    }

    [Fact]
    public void RecordTokensProcessed_RecordsCorrectValue()
    {
        var meterFactory = CreateMeterFactory();
        var metrics = new ChargebackMetrics(meterFactory);
        using var collector = new MetricCollector<long>(meterFactory, ChargebackMetrics.MeterName, "aipolicy.tokens_processed");

        metrics.RecordTokensProcessed(500, "t1", "app-1", "gpt-4o", "gpt-4o-deploy");

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Single(measurements);
        Assert.Equal(500, measurements[0].Value);
        Assert.Equal("t1", measurements[0].Tags["tenant_id"]);
        Assert.Equal("app-1", measurements[0].Tags["client_app_id"]);
        Assert.Equal("gpt-4o", measurements[0].Tags["model"]);
        Assert.Equal("gpt-4o-deploy", measurements[0].Tags["deployment_id"]);
    }

    [Fact]
    public void RecordRequest_RecordsCorrectValue()
    {
        var meterFactory = CreateMeterFactory();
        var metrics = new ChargebackMetrics(meterFactory);
        using var collector = new MetricCollector<long>(meterFactory, ChargebackMetrics.MeterName, "aipolicy.requests_processed");

        metrics.RecordRequest("t1", "app-1", "gpt-4o");

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Single(measurements);
        Assert.Equal(1, measurements[0].Value);
        Assert.Equal("t1", measurements[0].Tags["tenant_id"]);
        Assert.Equal("app-1", measurements[0].Tags["client_app_id"]);
        Assert.Equal("gpt-4o", measurements[0].Tags["model"]);
    }

    [Fact]
    public void RecordCost_RecordsCorrectValue()
    {
        var meterFactory = CreateMeterFactory();
        var metrics = new ChargebackMetrics(meterFactory);
        using var collector = new MetricCollector<double>(meterFactory, ChargebackMetrics.MeterName, "aipolicy.cost_total");

        metrics.RecordCost(1.25, "t1", "app-1", "gpt-4o");

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Single(measurements);
        Assert.Equal(1.25, measurements[0].Value);
        Assert.Equal("t1", measurements[0].Tags["tenant_id"]);
        Assert.Equal("app-1", measurements[0].Tags["client_app_id"]);
        Assert.Equal("gpt-4o", measurements[0].Tags["model"]);
    }

    [Fact]
    public void MultipleRecords_AccumulateCorrectly()
    {
        var meterFactory = CreateMeterFactory();
        var metrics = new ChargebackMetrics(meterFactory);
        using var collector = new MetricCollector<long>(meterFactory, ChargebackMetrics.MeterName, "aipolicy.tokens_processed");

        metrics.RecordTokensProcessed(100, "t1", "app-1", "gpt-4o", "deploy-1");
        metrics.RecordTokensProcessed(200, "t2", "app-1", "gpt-4", "deploy-2");

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Equal(2, measurements.Count);
        Assert.Equal(100, measurements[0].Value);
        Assert.Equal(200, measurements[1].Value);
    }
}
