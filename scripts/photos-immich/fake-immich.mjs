// A stand-in Immich for LOCAL EXERCISE ONLY (docs/photos-plan.md §2.4).
//
// This is a development fixture, not part of any deployment. It exists so `photos-sync-immich` can be
// driven end to end — against a throwaway SQLite database — without a container being up and without
// a build or a test ever touching a live sidecar. It serves the same routes and shapes the C# test
// suite's in-process stand-in serves.
//
//   node scripts/photos-immich/fake-immich.mjs --port 8099 --dataset <file.json>
//
// The dataset is JSON: { assets: [...], people: [...], faces: { assetId: [...] }, duplicates: [...] }.
// Boxes are given as fractions and re-emitted as PIXELS, because that is what Immich reports and what
// the client has to convert — a fixture that skipped the conversion would prove nothing about it.
//
// Loopback only. It binds 127.0.0.1 and refuses every request without the api key.

import http from "node:http";
import fs from "node:fs";

const args = process.argv.slice(2);
const opt = (name, fallback) => {
  const i = args.indexOf("--" + name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};

const port = Number(opt("port", "8099"));
const apiKey = opt("api-key", "test-key");
const datasetPath = opt("dataset", null);
const version = opt("version", "1.120.2").split(".").map(Number);

const data = datasetPath
  ? JSON.parse(fs.readFileSync(datasetPath, "utf8"))
  : { assets: [], people: [], faces: {}, duplicates: [] };

const IMAGE_W = 1000;
const IMAGE_H = 800;

const json = (res, body) => {
  const text = JSON.stringify(body);
  res.writeHead(200, { "content-type": "application/json" });
  res.end(text);
};

const readBody = (req) =>
  new Promise((resolve) => {
    let text = "";
    req.on("data", (chunk) => (text += chunk));
    req.on("end", () => {
      try {
        resolve(JSON.parse(text || "{}"));
      } catch {
        resolve({});
      }
    });
  });

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, "http://localhost");
  const path = url.pathname;

  if (req.headers["x-api-key"] !== apiKey) {
    res.writeHead(401).end();
    return;
  }

  if (path === "/api/server/version") {
    json(res, { major: version[0], minor: version[1], patch: version[2] });
    return;
  }

  if (path === "/api/search/metadata") {
    const body = await readBody(req);
    const page = Number(body.page || 1);
    const size = Number(body.size || 100);
    const skip = (page - 1) * size;
    const items = data.assets.slice(skip, skip + size).map((a) => ({
      id: a.id,
      originalPath: a.originalPath,
      exifInfo: { city: a.city ?? null, state: a.state ?? null, country: a.country ?? null },
    }));
    json(res, {
      assets: {
        items,
        nextPage: skip + items.length < data.assets.length ? String(page + 1) : null,
      },
    });
    return;
  }

  if (path === "/api/people") {
    const page = Number(url.searchParams.get("page") || 1);
    const size = Number(url.searchParams.get("size") || 100);
    const skip = (page - 1) * size;
    const people = data.people.slice(skip, skip + size).map((p) => ({ id: p.id, name: "" }));
    json(res, { people, hasNextPage: skip + people.length < data.people.length });
    return;
  }

  if (path.startsWith("/api/assets/")) {
    const id = decodeURIComponent(path.slice("/api/assets/".length));
    const faces = data.faces?.[id] ?? [];
    const byCluster = new Map();
    for (const face of faces) {
      if (!byCluster.has(face.personId)) byCluster.set(face.personId, []);
      byCluster.get(face.personId).push(face);
    }
    json(res, {
      id,
      people: [...byCluster.entries()].map(([personId, list]) => ({
        id: personId,
        name: "",
        faces: list.map((f, i) => ({
          id: `${id}:${personId}:${i}`,
          imageWidth: IMAGE_W,
          imageHeight: IMAGE_H,
          boundingBoxX1: Math.round((f.x ?? 0) * IMAGE_W),
          boundingBoxY1: Math.round((f.y ?? 0) * IMAGE_H),
          boundingBoxX2: Math.round(((f.x ?? 0) + (f.w ?? 0)) * IMAGE_W),
          boundingBoxY2: Math.round(((f.y ?? 0) + (f.h ?? 0)) * IMAGE_H),
          confidence: f.confidence ?? 0.9,
        })),
      })),
    });
    return;
  }

  if (path === "/api/duplicates") {
    json(res, (data.duplicates ?? []).map((g) => ({
      duplicateId: g.duplicateId,
      assets: g.assetIds.map((id) => ({ id })),
    })));
    return;
  }

  if (path.startsWith("/api/people/") && path.endsWith("/thumbnail")) {
    // A tiny opaque body stands in for a face crop: what the sync does with it is cache it, and the
    // cache does not care what the pixels are.
    res.writeHead(200, { "content-type": "image/jpeg" });
    res.end(Buffer.from([0xff, 0xd8, 0xff, 0xd9]));
    return;
  }

  res.writeHead(404).end();
});

server.listen(port, "127.0.0.1", () => {
  console.log(`fake-immich listening on http://127.0.0.1:${port} (${data.assets.length} assets, `
    + `${data.people.length} clusters, ${(data.duplicates ?? []).length} duplicate group(s))`);
});
