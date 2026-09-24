# Live blocking tree from loaded session evidence

The Live sessions panel now groups visible blocking requests by their resolved
blocker and nests dependent sessions into chains. Several requests from the
same session occupy one node when they agree on a blocker. The tree labels a
root session whose row is absent from the loaded page and retains special SQL
blockers as distinct roots.

The panel builds the tree only from the selected snapshot and loaded page.
Filters, paging, and a truncated collection can leave a blocker row out of
view. Requests that disagree about their blocker, and cycles in the visible
relationships, remain in explicit unresolved groups rather than being shown
under an invented head blocker. The detailed session table is unchanged.

Five focused model tests passed for branching, duplicate requests, missing
roots, special blockers, ambiguity, cycles, and empty evidence. TypeScript
checking passed. This change does not certify the UI against a live SQL Server
lab or make paged evidence a complete blocking graph.
