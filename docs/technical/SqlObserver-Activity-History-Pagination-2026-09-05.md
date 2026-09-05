# Activity history pagination

Implemented and deployed on WIN-QNGOV5GDM24 on 2026-09-05.

Blocking history now exposes **Older history**, **Newer history** and **First history page** controls. Each request remains bounded to 25 observations. Navigation preserves the first page's exact UTC window and sends the server's opaque continuation cursor; no offset paging or database migration is required. The page number, observation count and end-of-window state are displayed with the table.

Changing the target or history window resets navigation and aborts outstanding requests. Navigation is disabled while loading. Failed requests retain the displayed page and offer retry through the same controls. Current sessions, requests, waits and current blocking remain independent of history page navigation. The existing Overview layout and styles are preserved.

## Lab browser validation

- The 24-hour window returned 25 observations on page 1 and 13 on page 2.
- Page 2 exposed older blocking observations down to 2026-09-04T19:26:37.360727Z and disabled Older at the end.
- Newer returned exactly the original first-page text and UTC bounds.
- First history page also restored the original page.
- Changing the window reset pagination to page 1.
- Application and collector services remained running.

The deployed frontend is `C:\SqlObserverLab\web-history-pagination-20260905`. Server and collector binaries are unchanged. Rollback can restore the service's web-root argument to `web-replication-overview-v3-20260905` and restart SqlObserverServer.

## Checks and scope

Five focused activity tests passed, covering independent section failures, abort behavior, fixed UTC bounds, exact opaque-cursor encoding and sanitized page failures. Type checking, production build and asset verification passed. All 109 frontend tests passed. Full frontend results are saved in `TestResults/history-pagination/frontend-tests.txt`; browser evidence is in the same directory. This frontend change does not require rerunning the unchanged backend certification lanes.

Pagination covers blocking history within the existing 1-, 6- and 24-hour windows. Current sessions and waits still use their existing bounded first-page views. Larger time windows and other evidence surfaces are separate work.
