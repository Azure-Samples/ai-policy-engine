# Session: SPA Publish + Terraform Migration (2026-05-14T15:39:37Z)

## Request
User (Zack Way): Fix SPA publish, Cosmos firewall, and begin Terraform migration (remove Bicep scaffolding)

## Shipped
- SPA publish fix: `src/chargeback-ui/` production build now properly output to `wwwroot/spa/`
- Cosmos firewall: Port 10250 + 10255 exposed via Bicep; Cosmos connection now accepts host
- Bicep scaffolding removed: Empty/unused modules deleted; infrastructure prep for Terraform

## PR
- **URL:** https://github.com/Azure-Samples/ai-policy-engine/pull/29
- **Branch:** `fix/spa-publish-and-terraform-migration` (seiggy fork)
- **Status:** Open, awaiting review
