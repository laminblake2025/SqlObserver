# Alert acknowledgement retry identity

The web client now allocates one operation ID per selected target, alert, and
first-observed episode. A failed or uncertain request keeps that ID for the
next click, including after navigating within the running app. A confirmed
successful response clears it. The request client receives the ID instead of
creating a new one for every POST. The repository's `alerting.acknowledge`
function recognizes the repeated ID and returns the original audit receipt
when the request payload matches.

The focused runtime test verifies that a failed POST and its retry send the
same ID, that a distinct episode gets another ID, and that success releases
the ID. Existing alert client/UI tests and TypeScript checking passed. A full
browser reload starts a new in-memory attempt store, so retry continuity is
limited to the current app session.
