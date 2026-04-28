# AIPolicyEngine API Load Test

Load testing using [NBomber](https://nbomber.com/).

## Running

```bash
# Against deployed Container App
cd src/AIPolicyEngine.LoadTest
dotnet run -c Release

# Against custom URL
dotnet run -c Release -- https://your-container-app.azurecontainerapps.io
```

## Scenarios

| Scenario | Description | Load |
|----------|-------------|------|
| precheck | GET /api/precheck (authorization check) | 100 req/s for 30s |
| log_ingest | POST /api/log (usage recording) | 50 req/s for 30s |
| dashboard_read | GET /chargeback (dashboard data) | 20 req/s for 30s |
| apim_flow | Precheck → Log (simulates full APIM flow) | 50 req/s for 30s |

## Reports

HTML and Markdown reports are generated in the `reports/` folder after each run.
