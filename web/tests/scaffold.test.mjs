import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const configurationUrl = new URL("../tsconfig.json", import.meta.url);

test("the frontend keeps strict TypeScript enabled", async () => {
  const configuration = JSON.parse(await readFile(configurationUrl, "utf8"));

  assert.equal(configuration.compilerOptions.strict, true);
  assert.equal(configuration.compilerOptions.noUncheckedIndexedAccess, true);
  assert.equal(configuration.compilerOptions.noEmit, true);
});
