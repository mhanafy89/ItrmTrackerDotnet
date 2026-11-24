// Legacy oracle for the parity check: loads the ORIGINAL JavaScript modules of the Node.js app (with the database
// module stubbed out) and evaluates every case from cases.json, writing the results to results.json.
// Usage: node legacy-oracle.js <legacyAppRoot> <cases.json> <results.json>
"use strict";
const fs = require("fs");
const path = require("path");
const Module = require("module");

const [root, casesFile, resultsFile] = process.argv.slice(2);
const abs = (p) => path.join(root, p);

// --- stub the database module so lib/sla.js, lib/auth.js and routes/import.js can be loaded without mssql ---
const dbPath = require.resolve(abs("db/database.js"));
require.cache[dbPath] = {
  id: dbPath, filename: dbPath, loaded: true,
  exports: { query: async () => ({ rows: [] }), queryOne: async () => null, transaction: async (fn) => fn({}), isUniqueViolation: () => false, isReferenceViolation: () => false },
};

// Loads a module and exposes selected internal (non-exported) functions.
function loadWithInternals(rel, names) {
  const file = abs(rel);
  const src = fs.readFileSync(file, "utf8") + `\nmodule.exports.__internals = { ${names.join(", ")} };`;
  const m = new Module(file, module);
  m.filename = file;
  m.paths = Module._nodeModulePaths(path.dirname(file));
  m._compile(src, file);
  return m.exports.__internals;
}

const E = require(abs("lib/sla-engine.js"));
const legacySla = require(abs("lib/sla.js"));
const validation = require(abs("routes/validation.js"));
const { parseCsv } = require(abs("lib/csv-reader.js"));
const auth = require(abs("lib/auth.js"));
const imp = loadWithInternals("routes/import.js", ["normalizeDate", "parseBool", "normalizeHeader", "suggestMapping"]);
const query = loadWithInternals("routes/query.js", ["checkReadOnly"]);
const slaRoute = require(abs("routes/sla.js"));

const d = (ms) => (ms === null || ms === undefined ? null : new Date(ms));
const stageRow = (s) => ({ ...s, started_at: d(s.started_at), completed_at: d(s.completed_at), paused_at: d(s.paused_at), resume_at: d(s.resume_at) });
const seg = (w) => ({ ...w, started_at: d(w.started_at), ended_at: d(w.ended_at) });
const pau = (p) => ({ ...p, paused_at: d(p.paused_at), resumed_at: d(p.resumed_at) });
const ms = (v) => (v instanceof Date ? v.getTime() : v);

const handlers = {
  workingDays: (c) => E.workingDaysBetween(d(c.from), d(c.to), c.weekend),
  addWorkingDays: (c) => E.addWorkingDays(c.start, c.days, c.weekend),
  stageSla: (c) => {
    const m = E.calculateStageSLA({
      stage: stageRow(c.stage), workSegments: c.work.map(seg), pauses: c.pauses.map(pau),
      priorityFactor: c.factor, settings: E.parseEngineSettings(c.settings), weekend: c.weekend, now: d(c.now),
    });
    return { ...m, startedAt: ms(m.startedAt), completedAt: ms(m.completedAt), pausedAt: ms(m.pausedAt), resumedAt: ms(m.resumedAt) };
  },
  summarize: (c) => {
    const metrics = c.stages.map((s) => E.calculateStageSLA({
      stage: stageRow(s.stage), workSegments: s.work.map(seg), pauses: s.pauses.map(pau),
      priorityFactor: c.factor, settings: E.parseEngineSettings(c.settings), weekend: c.weekend, now: d(c.now),
    }));
    const sum = E.summarizeRequest({ stageMetrics: metrics, settings: E.parseEngineSettings(c.settings), weekend: c.weekend, now: d(c.now) });
    const cur = E.deriveCurrentStage(c.stages.map((s) => stageRow(s.stage)));
    return { ...sum, current: cur ? cur.stage_name : null };
  },
  plan: (c) => {
    const r = E.planTransition({ stage: c.stage, toStatus: c.toStatus, stages: c.stages, plannedEffort: c.plannedEffort ?? undefined, settings: E.parseEngineSettings(c.settings), force: c.force });
    return { action: r.action, toStatus: r.toStatus, errors: r.errors, plannedEffort: r.plannedEffort ?? null };
  },
  validateStageInput: (c) => {
    const body = {};
    for (const k of ["stage", "status", "plannedEffort", "actualEffortDays", "note"]) if (c[k] !== null && c[k] !== undefined) body[k] = c[k];
    const r = E.validateStageStatusInput(body);
    return { errors: r.errors, stage: r.clean.stage, status: r.clean.status, plannedEffort: r.clean.plannedEffort ?? null, actualEffortDays: r.clean.actualEffortDays ?? null, note: r.clean.note ?? null };
  },
  engineSettings: (c) => {
    const parsed = E.parseEngineSettings(c.raw);
    const v = E.validateEngineSettings(c.raw);
    return { parsed, errors: v.errors, clean: v.clean };
  },
  initialStages: (c) => E.planInitialStages({ pipeline: c.pipeline, legacyStage: c.legacyStage, intent: E.classifyLegacyStatus(c.statusName, c.statusState) })
    .map((p) => ({ ...p, intent: E.classifyLegacyStatus(c.statusName, c.statusState) })),
  legacySla: (c) => {
    const idx = legacySla.buildIndex(c.config);
    const r = legacySla.computeSla(c.record, c.type, idx, c.today);
    return r;
  },
  validateRequest: (c) => (c.incident ? validation.validateIncident(c.body) : validation.validateBusinessRequest(c.body)),
  normalizeDate: (c) => imp.normalizeDate(c.value),
  parseBool: (c) => imp.parseBool(c.value),
  normalizeHeader: (c) => imp.normalizeHeader(c.value),
  suggestMapping: (c) => imp.suggestMapping(c.target, c.headers),
  checkReadOnly: (c) => query.checkReadOnly(c.sql),
  csv: (c) => parseCsv(c.text),
  scryptHash: (c) => auth.hashPassword(c.password),
  slaConfigValidate: (c) => {
    const r = slaRoute.validate(c.body);
    return r.errors ? { errors: r.errors } : { errors: [], clean: r.clean };
  },
  scenario: (c) => runScenario(c),
};

// ---- SLA service scenarios: the ORIGINAL SlaService + MemorySlaRepository driven step by step ----
const { SlaService, SlaError } = require(abs("lib/sla-service.js"));
const { MemorySlaRepository } = require(abs("test/memory-repository.js"));

// Dates -> epoch ms, recursively (the C# side does the same), so both outputs compare structurally.
const plain = (v) => JSON.parse(JSON.stringify(v, function (k, val) { const raw = this[k]; return raw instanceof Date ? raw.getTime() : val; }));
const pick = (o, keys) => Object.fromEntries(keys.map((k) => [k, o[k] === undefined ? null : o[k]]));

function jsConfig(cfg) {
  return { ...cfg, categories: [], settings: { ...cfg.settings } };
}

function runScenarioAsync(c) {
  const repo = new MemorySlaRepository();
  let config = jsConfig(c.config);
  const clock = { now: null };
  const service = new SlaService({ repo, getConfig: async () => config, now: () => clock.now });
  for (const r of c.requests) repo.addRequest({ id: r.id, priority: r.priority, stage: r.stage, status: r.status });

  const statusRow = (h) => pick(h, ["request_stage_id", "business_request_id", "stage_name", "from_status", "to_status", "action", "source", "planned_effort_days", "consumed_days", "remaining_days", "note", "changed_by", "changed_at"]);
  const stageMove = (h) => pick(h, ["business_request_id", "from_stage", "to_stage", "reason", "source", "changed_by", "changed_at"]);
  const pauseRow = (p) => pick(p, ["request_stage_id", "business_request_id", "paused_at", "resumed_at", "hold_days", "reason", "paused_by", "resumed_by"]);

  return (async () => {
    const outputs = [];
    for (const step of c.steps) {
      clock.now = new Date(step.at);
      try {
        let out;
        switch (step.op) {
          case "get": out = await service.getRequestSla(step.id); break;
          case "update": {
            const body = {};
            for (const k of ["stage", "status", "plannedEffort", "actualEffortDays", "note"]) if (step.input[k] !== null && step.input[k] !== undefined) body[k] = step.input[k];
            out = await service.updateStageStatus(step.id, body, step.user);
            break;
          }
          case "legacy": {
            const r = repo.state.requests.get(step.id);
            r.stage = step.stage;
            r.status = step.status;
            const res = await service.syncFromLegacy(step.id, step.user);
            out = { initialized: res.initialized };
            break;
          }
          case "backfill": out = await service.backfillMissing({ user: "sla-migration", logger: { error() {} } }); break;
          case "dashboard": out = await service.getDashboard({ priority: step.priority || undefined, stage: step.stage || undefined, health: step.health || undefined }); break;
          case "history": {
            const h = await service.getHistory(step.id);
            out = { statusHistory: h.statusHistory.map(statusRow), stageHistory: h.stageHistory.map(stageMove), pauses: h.pauses.map(pauseRow) };
            break;
          }
          case "config": config = jsConfig(step.config); out = { ok: true }; break;
          default: throw new Error("unknown op " + step.op);
        }
        outputs.push(plain(out));
      } catch (err) {
        if (err instanceof SlaError) outputs.push({ error: err.statusCode, errors: err.errors });
        else throw err;
      }
    }
    const s = repo.state;
    return plain({
      outputs,
      state: {
        requests: [...s.requests.values()].map((r) => ({ id: r.id, stage: r.stage, status: r.status })),
        stages: s.stages.map((x) => pick(x, ["id", "business_request_id", "stage_name", "stage_order", "status", "stage_effort_days", "actual_effort_days", "remaining_effort_days", "sla_consumed_days", "total_hold_time", "started_at", "completed_at", "paused_at", "resume_at", "reopen_count", "is_backfilled", "updated_by", "updated_at"])),
        work: s.work.map((w) => pick(w, ["id", "request_stage_id", "business_request_id", "started_at", "ended_at", "end_reason", "working_days", "created_by"])),
        pauses: s.pauses.map(pauseRow),
        statusHistory: s.statusHistory.map(statusRow),
        stageHistory: s.stageHistory.map(stageMove),
      },
    });
  })();
}
let pendingScenarios = [];
function runScenario(c) {
  const p = runScenarioAsync(c);
  pendingScenarios.push(p);
  return p;
}

const cases = JSON.parse(fs.readFileSync(casesFile, "utf8"));
(async () => {
  const results = [];
  for (const c of cases) {
    try {
      results.push({ ok: true, value: await handlers[c.kind](c.input) });
    } catch (err) {
      results.push({ ok: false, error: String(err && err.message) });
    }
  }
  fs.writeFileSync(resultsFile, JSON.stringify(results));
  console.log(`oracle: ${results.length} cases evaluated`);
})();
