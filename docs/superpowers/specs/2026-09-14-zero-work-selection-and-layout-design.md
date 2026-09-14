# Zero-work selection and backup-page layout design

## Outcome

After discovery, a checkbox changes only in-memory selection state and the affected bound rows. It never reads the filesystem, recalculates migration coverage, rebuilds finding groups, or refreshes an entire grid. Full validation runs only when the user explicitly requests it or starts a backup.

## Selection data flow

Session and source relationships remain precomputed by `SelectionCoordinator`. `SourceItem` publishes property changes so linked source checkboxes update without `ICollectionView.Refresh`. Direct checkbox click handlers replace `DataGrid.CellEditEnding`, avoiding edit-commit re-entry. A single click updates the row, its session references, reference counts, tab counters, and a short “selection changed” message. Selected-only filters may refresh their own grid because membership genuinely changes; ordinary views do not refresh.

The WPF smoke test must fail if a checkbox schedules deferred coverage work. It also times a 1,000-session batch and a single item after the dispatcher has processed pending work.

## Visual direction

The backup page uses a calm migration-console layout: slate application background, white content cards, a dark navy header, teal primary actions, amber review states, and red only for blocking or destructive risk. Microsoft YaHei UI carries Chinese body text; Segoe UI Semibold is used for compact status labels.

The page is organized into four visible stages: scan source, readiness, choose personal data, and choose destination. Each stage is a card with a short heading and one action area. The selection card gets the largest space. Tabs use equal spacing and a quiet selected state. Session rows show only status, title, activity, project and reminder; identifiers and transcript paths move out of the primary table. Findings are collapsed by default behind a count because they are supporting detail.

## Safety

Mandatory sources remain locked. Session selection still closes over its transcript, project and dependency sources. Deselecting a shared source clears only related selected sessions. No source files are changed during selection. Backup start performs a fresh coverage check before any destination write.

