import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test from "node:test";

// Exercise the dependency copy that Vite's CSS pipeline resolves.
const require = createRequire(import.meta.url);
const requireVite = createRequire(require.resolve("vite/package.json"));
const requirePostcss = createRequire(requireVite.resolve("postcss/package.json"));
const postcss = requireVite("postcss");
const { SourceMapConsumer } = requirePostcss("source-map-js");
const flatMap = { version: 3, sources: ["source.css"], names: [], mappings: "AAAA" };
const indexedMap = (line, map = flatMap) => ({ version: 3, sections: [{ offset: { line, column: 0 }, map }] });

test("CSS toolchain rejects source-map offsets that amplify serialization work", () => {
  // Constructor rejection stays bounded even if a vulnerable dependency returns.
  for (const line of [1_000_000_000, "1000000000", -1, 0.5]) {
    const map = indexedMap(line);
    assert.throws(() => new SourceMapConsumer(map), /Section offset/);
    assert.throws(() => new SourceMapConsumer(JSON.stringify(map)), /Section offset/);
  }
});

test("CSS toolchain rejects oversized totals across nested indexed maps", () => {
  assert.throws(() => new SourceMapConsumer(indexedMap(6_000_000, indexedMap(6_000_000))), /Section offset/);
});

test("CSS processing preserves ordinary source maps", () => {
  const css = "a { color: teal; }";
  const result = postcss([]).process(css, {
    from: "generated.css",
    to: "output.css",
    map: { inline: false, prev: JSON.stringify(flatMap) },
  }).sync();
  assert.ok(result.css.startsWith(css));
  assert.ok(result.map.toJSON().sources.includes("source.css"));
  const consumer = new SourceMapConsumer(result.map.toString());
  assert.deepEqual(consumer.originalPositionFor({ line: 1, column: 0 }), {
    source: "source.css", line: 1, column: 0, name: null,
  });
});
