// Playwright's Node request fixture cannot negotiate the current Windows token.
// Use the authenticated same-origin browser for owner-only lab API checks.
export async function ownerBrowserRequest(page) {
  await page.goto('/__lab/status');
  const send = async (path, options = {}, method = 'GET') => {
    const result = await page.evaluate(async ({ path, data, method }) => {
      const response = await fetch(path, {
        method, credentials: 'same-origin',
        ...(data === undefined ? {} : { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) }),
      });
      return { status: response.status, body: await response.text() };
    }, { path, data: options.data, method });
    return { status: () => result.status, json: async () => JSON.parse(result.body) };
  };
  return { get: (path) => send(path), post: (path, options) => send(path, options, 'POST') };
}