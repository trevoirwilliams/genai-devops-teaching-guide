# OrderService Incident Runbook

## Immediate checks
1. Confirm whether the latest deployment changed the container image or startup configuration.
2. Validate application health and endpoint error rate.
3. If production impact continues, roll back to the last healthy revision.

## Safe rollback guidance
- Revert traffic to the previous healthy revision.
- Do not continue rollout until error rate normalizes.
- Validate /orders endpoint and queue publish path.

## Validation after mitigation
- Error rate below 1%
- p95 latency below 500 ms
- No new deployment failures in CI/CD logs