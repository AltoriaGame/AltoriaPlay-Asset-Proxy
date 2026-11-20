 "use strict";

const http = require("node:http");
const https = require("node:https");
const crypto = require("node:crypto");
const path = require("node:path");
const { Transform } = require("node:stream");

const CONFIG = {
  HOST: process.env.HOST || "127.0.0.1",
  PORT: Number(process.env.PORT || 3000),
  SEAWEEDFS_URL: process.env.SEAWEEDFS_URL || "http://127.0.0.1:8888",
  API_KEY: process.env.API_KEY || "",
  MAX_UPLOAD_SIZE: Number(process.env.MAX_UPLOAD_SIZE || 2147483648),
  DEFAULT_BUCKET: process.env.DEFAULT_BUCKET || "retoria-platform-assets-prod",
  CORS_ORIGINS: (process.env.CORS_ORIGINS || "")
    .split(",").map(v => v.trim()).filter(Boolean)
};

if (CONFIG.API_KEY.length < 32) {
  throw new Error("API_KEY must contain at least 32 characters.");
}
if (!Number.isSafeInteger(CONFIG.PORT) || CONFIG.PORT < 1 || CONFIG.PORT > 65535) {
  throw new Error("PORT must be a valid TCP port.");
}
if (!Number.isSafeInteger(CONFIG.MAX_UPLOAD_SIZE) || CONFIG.MAX_UPLOAD_SIZE < 1) {
  throw new Error("MAX_UPLOAD_SIZE must be a positive integer.");
}

const SEAWEEDFS = new URL(CONFIG.SEAWEEDFS_URL);
if (!["http:", "https:"].includes(SEAWEEDFS.protocol)) {
  throw new Error("SEAWEEDFS_URL must use HTTP or HTTPS.");
}
const transport = SEAWEEDFS.protocol === "https:" ? https : http;

const ALLOWED_METHODS = new Set(["GET", "HEAD", "PUT", "POST", "DELETE"]);
const ALLOWED_QUERY = new Set([
  "pretty", "limit", "lastFileName", "recursive",
  "collection", "replication", "ttl", "fsync"
]);

function sendJson(res, status, value) {
  if (res.headersSent) return;
  const body = Buffer.from(JSON.stringify(value, null, 2));
  res.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Content-Length": body.length,
    "Cache-Control": "no-store"
  });
  res.end(body);
}

function authenticate(req) {
  const auth = req.headers.authorization || "";
  const bearer = auth.startsWith("Bearer ") ? auth.slice(7) : "";
  const supplied = req.headers["x-api-key"] || bearer;
  if (typeof supplied !== "string") return false;

  const a = Buffer.from(CONFIG.API_KEY);
  const b = Buffer.from(supplied);
  return a.length === b.length && crypto.timingSafeEqual(a, b);
}

function applyCors(req, res) {
  const origin = req.headers.origin;
  if (!origin) return true;
  if (!CONFIG.CORS_ORIGINS.includes(origin)) return false;

  res.setHeader("Access-Control-Allow-Origin", origin);
  res.setHeader("Vary", "Origin");
  res.setHeader("Access-Control-Allow-Headers", "Authorization, Content-Type, X-API-Key");
  res.setHeader("Access-Control-Allow-Methods", "GET, HEAD, POST, PUT, DELETE, OPTIONS");
  res.setHeader("Access-Control-Max-Age", "600");
  return true;
}

function decodeSegment(segment) {
  let value;
  try {
    value = decodeURIComponent(segment);
  } catch {
    throw new Error("Invalid URL encoding.");
  }

  if (!value || value === "." || value === ".." ||
      /[\/\\\0?#]/.test(value)) {
    throw new Error("Invalid path segment.");
  }
  return value;
}

function validBucket(bucket) {
  return /^[a-z0-9][a-z0-9._-]{1,62}[a-z0-9]$/i.test(bucket) &&
    !bucket.includes("..");
}

function buildQuery(searchParams) {
  const result = new URLSearchParams();
  for (const [key, value] of searchParams) {
    if (ALLOWED_QUERY.has(key)) result.set(key, value);
  }
  return result.toString();
}

function uploadLimiter(maxBytes) {
  let total = 0;
  return new Transform({
    transform(chunk, encoding, callback) {
      total += chunk.length;
      if (total > maxBytes) {
        const error = new Error("Maximum upload size exceeded.");
        error.statusCode = 413;
        callback(error);
      } else {
        callback(null, chunk);
      }
    }
  });
}

function proxyToSeaweed(req, res, targetPath, query) {
  const target = new URL(targetPath, CONFIG.SEAWEEDFS_URL);
  if (query) target.search = query;

  const headers = {};
  if (req.headers["content-type"]) headers["content-type"] = req.headers["content-type"];
  if (req.headers["content-length"]) headers["content-length"] = req.headers["content-length"];

  if (["PUT", "POST"].includes(req.method) &&
      !headers["content-type"] && !targetPath.endsWith("/")) {
    headers["content-type"] =
      ({ ".glb": "model/gltf-binary", ".gltf": "model/gltf+json",
         ".stl": "model/stl", ".obj": "text/plain" })[path.extname(targetPath).toLowerCase()]
      || "application/octet-stream";
  }

  const upstream = transport.request(target, {
    method: req.method,
    headers,
    timeout: 120000
  }, upstreamRes => {
    const responseHeaders = {};
    const hopByHop = new Set([
      "connection", "keep-alive", "transfer-encoding", "upgrade",
      "proxy-authenticate", "proxy-authorization", "te", "trailer"
    ]);

    for (const [key, value] of Object.entries(upstreamRes.headers)) {
      if (!hopByHop.has(key.toLowerCase()) && value !== undefined) {
        responseHeaders[key] = value;
      }
    }
    responseHeaders["X-Content-Type-Options"] = "nosniff";
    responseHeaders["Cache-Control"] = "private, no-store";
    res.writeHead(upstreamRes.statusCode || 502, responseHeaders);
    upstreamRes.pipe(res);
  });

  upstream.on("timeout", () => upstream.destroy(new Error("SeaweedFS request timed out.")));
  upstream.on("error", error => {
    if (!res.headersSent) sendJson(res, 502, {
      error: "SeaweedFS connection failed.",
      message: error.message
    });
    else res.destroy(error);
  });

  if (["PUT", "POST"].includes(req.method)) {
    const declared = req.headers["content-length"];
    if (declared && Number(declared) > CONFIG.MAX_UPLOAD_SIZE) {
      upstream.destroy();
      req.resume();
      return sendJson(res, 413, { error: "Maximum upload size exceeded." });
    }

    const limiter = uploadLimiter(CONFIG.MAX_UPLOAD_SIZE);
    limiter.on("error", error => {
      upstream.destroy();
      if (!res.headersSent) sendJson(res, error.statusCode || 413, { error: error.message });
      else res.destroy(error);
    });
    req.pipe(limiter).pipe(upstream);
  } else {
    upstream.end();
  }
}

function handleRequest(req, res) {
  if (!applyCors(req, res)) {
    return sendJson(res, 403, { error: "Origin not allowed." });
  }

  if (req.method === "OPTIONS") {
    res.writeHead(204);
    return res.end();
  }

  let url;
  try {
    url = new URL(req.url, `http://${req.headers.host || "localhost"}`);
  } catch {
    return sendJson(res, 400, { error: "Invalid request URL." });
  }

  if (url.pathname === "/health" && req.method === "GET") {
    return sendJson(res, 200, {
      status: "online",
      service: "Retoria Asset Proxy",
      version: "1.0.0"
    });
  }

  if (!authenticate(req)) {
    return sendJson(res, 401, { error: "Unauthorized. A valid API key is required." });
  }

  if (!ALLOWED_METHODS.has(req.method)) {
    res.setHeader("Allow", [...ALLOWED_METHODS].join(", "));
    return sendJson(res, 405, { error: "Method not allowed." });
  }

  if (url.pathname === "/api/buckets") {
    return proxyToSeaweed(req, res, "/buckets/", buildQuery(url.searchParams));
  }

  const match = url.pathname.match(/^\/api\/buckets\/([^/]+)(?:\/(.*))?$/);
  if (!match) return sendJson(res, 404, { error: "API endpoint not found." });

  let bucket;
  let segments;
  try {
    bucket = decodeSegment(match[1]);
    segments = (match[2] || "").split("/").filter(Boolean).map(decodeSegment);
  } catch (error) {
    return sendJson(res, 400, { error: error.message });
  }

  if (!validBucket(bucket)) return sendJson(res, 400, { error: "Invalid bucket name." });

  let targetPath = "/buckets/" + encodeURIComponent(bucket);
  if (segments.length) {
    targetPath += "/" + segments.map(encodeURIComponent).join("/");
  }

  const directory = (match[2] || "").endsWith("/") || segments.length === 0;
  if (directory) targetPath += "/";

  return proxyToSeaweed(req, res, targetPath, buildQuery(url.searchParams));
}

const server = http.createServer((req, res) => {
  try {
    handleRequest(req, res);
  } catch (error) {
    console.error("[Retoria Asset Proxy]", error);
    if (!res.headersSent) sendJson(res, 500, { error: "Internal server error." });
    else res.destroy(error);
  }
});

server.requestTimeout = 0;
server.headersTimeout = 60000;

server.listen(CONFIG.PORT, CONFIG.HOST, () => {
  console.log("--------------------------------------");
  console.log(" Retoria Asset Proxy API");
  console.log("--------------------------------------");
  console.log(` Listening: http://${CONFIG.HOST}:${CONFIG.PORT}`);
  console.log(` SeaweedFS: ${CONFIG.SEAWEEDFS_URL}`);
  console.log(` Default bucket: ${CONFIG.DEFAULT_BUCKET}`);
  console.log(" Authentication: API key enabled");
  console.log("--------------------------------------");
});

process.on("SIGINT", () => {
  console.log("\nShutting down...");
  server.close(() => process.exit(0));
});
