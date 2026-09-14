# Responsive selection and safe cleanup implementation plan

1. Reproduce the dispatcher stall and identify repeated relationship, grid, and coverage work.
2. Add regression tests for reference-counted selection, bulk performance, backup scope, and cleanup boundaries.
3. Add the selection coordinator and replace per-row full refreshes with one batched refresh.
4. Coalesce coverage refreshes and remove repeated coverage evaluation from a single UI update.
5. Add category tabs, counts, virtualization, and lifecycle bulk actions.
6. Limit required backup scope to data that cannot be recreated after reinstall.
7. Add archived-project cleanup discovery, verified-package gating, quarantine journals, and safe restoration.
8. Run core tests, Release build, WPF stress smoke, self-test, real scan, encoding checks, and release packaging.
