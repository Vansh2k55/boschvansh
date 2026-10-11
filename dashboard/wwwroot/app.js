// Pitstop resilience console.
//
// Everything rendered here comes from /api/state, which reads live Docker
// status, live HTTP probes and the evidence files on disk. Nothing on this page
// is sampled, seeded or faked: when a value is missing the UI says so rather
// than inventing one.

const el = (id) => document.getElementById(id);
let state = null;
let lastGoodFetch = null;
let currentTarget = null;

async function api(path, options) {
  const response = await fetch(path, options);
  if (!response.ok) {
    const body = await response.json().catch(() => ({ error: response.statusText }));
    throw new Error(body.error || `Request failed (${response.status})`);
  }
  return response.status === 204 ? null : response.json();
}

async function refresh() {
  try {
    state = await api('/api/state');
    lastGoodFetch = new Date();
    render();
  } catch (err) {
    const f = el('freshness');
    f.textContent = `backend unreachable — ${err.message}`;
    f.classList.add('stale');
  }
  // Poll faster while something is actually happening.
  const next = state && state.run && state.run.isRunning ? 1000 : 2500;
  setTimeout(refresh, next);
}

function render() {
  renderFreshness();
  renderServices();
  renderModules();
  renderGraph();
  renderAssessment();
  renderTopology();
  renderGaps();
  renderRuns();
  renderExperiments();
  renderKillSwitch();
  renderTimeline();
  renderMilestones();
  populateChartRuns();
}

function renderFreshness() {
  const f = el('freshness');
  const age = lastGoodFetch ? Math.round((Date.now() - lastGoodFetch) / 1000) : null;
  f.classList.toggle('stale', age !== null && age > 10);
  f.textContent = age === null ? 'no data' : `updated ${age}s ago · ${lastGoodFetch.toLocaleTimeString()}`;
}

// Health classification. "no healthcheck" is deliberately its own state: the
// image declares no probe, so we do not know, and claiming healthy would be a
// lie the operator cannot see through.
function serviceClass(svc) {
  if (!svc.running) return 'fail';
  if (svc.health === 'healthy') return 'ok';
  if (svc.health === 'starting') return 'warn';
  if (svc.health === 'unhealthy') return 'fail';
  return 'unknown';
}

function renderServices() {
  const container = el('services');
  const services = state.services || [];
  if (!services.length) {
    container.innerHTML = '<p class="empty">Docker reported no services. Is the stack running?</p>';
    el('svc-summary').textContent = '';
    return;
  }
  const counts = services.reduce((acc, s) => { acc[serviceClass(s)] = (acc[serviceClass(s)] || 0) + 1; return acc; }, {});
  el('svc-summary').textContent =
    `${counts.ok || 0} healthy · ${counts.warn || 0} starting · ${counts.fail || 0} failed · ${counts.unknown || 0} not measured`;

  container.innerHTML = services.map((s) => {
    const cls = serviceClass(s);
    const detail = s.running ? s.health : s.state;
    return `<div class="svc ${cls}"><i class="dot s-${cls}"></i>
      <span class="name">${s.name}</span><span class="state">${detail}</span></div>`;
  }).join('');
}

function renderModules() {
  const tbody = el('modules');
  tbody.innerHTML = (state.modules || []).map((m) => {
    const cls = m.healthy ? 'ok' : 'fail';
    const status = m.statusCode === null ? 'timeout' : m.statusCode;
    const label = m.healthy ? 'serving' : (m.offlinePage ? 'offline page' : (m.error || 'unreachable'));
    return `<tr>
      <td>${m.name}</td>
      <td><i class="dot s-${cls}"></i> ${status}</td>
      <td class="num">${m.latencyMs} ms</td>
      <td style="color:var(--text-dim)">${label}</td>
    </tr>`;
  }).join('');

  // Data-integrity surface. Only a run that exercised a write can report this,
  // so when no run has, we say "not measured" rather than showing a green tick.
  const run = (state.runs || []).find((r) => r.divergenceDetected !== null && r.divergenceDetected !== undefined);
  const box = el('integrity');
  if (!run) {
    box.innerHTML = `<div class="metric-row"><span class="label">Write/event integrity</span>
      <span class="value" style="color:var(--text-faint)">not measured</span></div>`;
    return;
  }
  const bad = run.divergenceDetected === true;
  box.innerHTML = `<div class="metric-row">
      <span class="label">Write/event integrity <span style="color:var(--text-faint)">(last broker run ${run.runId})</span></span>
      <span class="value" style="color:${bad ? 'var(--fail)' : 'var(--ok)'}">${bad ? 'DIVERGENCE' : 'consistent'}</span>
    </div>
    ${bad ? '<p style="margin:2px 0 0;font-size:12px;color:var(--text-dim)">A customer row was committed while its event was never published.</p>' : ''}`;
}

// Layered layout. A force-directed graph looks busy and tells you nothing;
// tiers show how a request actually flows through the system.
function tierOf(node, graph) {
  if (node.name === 'webapp') return 0;
  if (node.kind === 'infrastructure') return 2;
  const hasRestIn = graph.edges.some((e) => e.to === node.name && e.kind === 'rest');
  return hasRestIn ? 1 : 3;
}

function renderGraph() {
  const graph = state.graph;
  const box = el('graph');
  if (!graph || !graph.nodes) return;

  el('graph-note').textContent = `${graph.nodes.length} services · ${graph.edges.length} dependencies`;

  const tiers = [[], [], [], []];
  graph.nodes.forEach((n) => tiers[tierOf(n, graph)].push(n));

  const colW = 170, rowH = 38, padX = 12, padY = 18;
  const height = Math.max(...tiers.map((t) => t.length)) * rowH + padY * 2;
  const width = colW * 4;
  const pos = {};
  tiers.forEach((tier, ti) => {
    const offset = (height - tier.length * rowH) / 2;
    tier.forEach((n, ni) => {
      pos[n.name] = { x: ti * colW + padX, y: offset + ni * rowH, w: colW - padX * 2 - 10, h: 24 };
    });
  });

  const edges = graph.edges.map((e) => {
    const a = pos[e.from], b = pos[e.to];
    if (!a || !b) return '';
    const x1 = a.x + a.w, y1 = a.y + a.h / 2, x2 = b.x, y2 = b.y + b.h / 2;
    const mx = (x1 + x2) / 2;
    const isEvent = e.kind === 'publish' || e.kind === 'subscribe';
    return `<path class="edge ${isEvent ? 'event' : ''}" d="M${x1},${y1} C${mx},${y1} ${mx},${y2} ${x2},${y2}" />`;
  }).join('');

  const nodes = graph.nodes.map((n) => {
    const p = pos[n.name];
    const classes = ['node'];
    if (n.kind === 'infrastructure') classes.push('infra');
    if (currentTarget && n.name === currentTarget) classes.push('target');
    else if (currentTarget) classes.push('unaffected');
    const label = n.name.length > 20 ? n.name.slice(0, 19) + '…' : n.name;
    return `<g class="${classes.join(' ')}">
      <rect x="${p.x}" y="${p.y}" width="${p.w}" height="${p.h}"></rect>
      <text x="${p.x + 7}" y="${p.y + 16}">${label}</text>
    </g>`;
  }).join('');

  box.innerHTML = `<svg class="graph" viewBox="0 0 ${width} ${height}" role="img"
      aria-label="Service dependency map">${edges}${nodes}</svg>
    ${currentTarget ? `<p style="margin:9px 0 0;font-size:12px;color:var(--text-dim)">
      Highlighted: <strong style="color:var(--fail)">${currentTarget}</strong> is the live experiment target.
      Everything else is expected to stay healthy &mdash; the monitoring table above is what confirms it.</p>` : ''}`;
}

function renderAssessment() {
  const box = el('assessment');
  const a = state.assessment;
  if (!a || !a.Experiments || !a.Experiments.length) {
    box.innerHTML = '<p class="empty">No assessment yet. Run an experiment, or run <code>analyze.ps1</code>.</p>';
    return;
  }

  const experiments = a.Experiments.map((e) => {
    const score = e.RepresentativeScore;
    const colour = score === null ? 'var(--text-faint)' : score >= 75 ? 'var(--ok)' : score >= 55 ? 'var(--warn)' : 'var(--fail)';
    const never = (e.NeverMeasured || []).length
      ? `<div class="score-meta" style="color:var(--warn)">Never measured: ${e.NeverMeasured.join(', ')}. Unmeasured is not the same as passing.</div>`
      : '';
    return `<div style="flex:1;min-width:210px">
      <div class="score-block">
        <div class="score" style="color:${colour}">${score === null ? '—' : score}<small>/100</small></div>
        <div>
          <div style="font-family:var(--mono);font-size:13px">${e.Experiment}</div>
          <div class="score-meta">${e.ScoreBasis}</div>
          <div class="score-meta">${e.CredibleRuns} credible of ${e.TotalRuns} run(s)</div>
        </div>
      </div>
      ${never}
    </div>`;
  }).join('');

  const comparison = a.Comparison
    ? `<p style="margin:4px 0 14px;font-size:12.5px;color:var(--text-dim)">
        Weakest: <strong style="color:var(--fail)">${a.Comparison.WeakestExperiment}</strong>
        &mdash; ${a.Comparison.Ranking.join(' · ')}. ${a.Comparison.Note}</p>`
    : '';

  const findings = (a.ObservedFindings || []).map((f) => `
    <div class="finding">
      <div class="head">
        <span class="pill ${String(f.Severity).toLowerCase()}">${f.Severity}</span>
        <span class="pill observed">observed</span>
        <span class="pill ${f.Confidence}">${f.Confidence} confidence</span>
        <span class="title">${f.Title}</span>
      </div>
      <p>${f.Hypothesis}</p>
      <div class="evidence">${(f.Evidence || []).join(' · ')}</div>
      <p><strong style="color:var(--text)">Fix:</strong> ${f.Recommendation}</p>
      <p style="font-size:11.5px;color:var(--text-faint)">Reproduced in ${f.ReproducedInRuns} run(s).</p>
    </div>`).join('');

  box.innerHTML = `<div style="display:flex;gap:22px;flex-wrap:wrap">${experiments}</div>
    ${comparison}
    ${findings || '<p class="empty">No findings from the deterministic rules.</p>'}`;
}

function renderTopology() {
  const box = el('topology');
  const risks = (state.assessment && state.assessment.InferredTopologyRisks) || [];
  if (!risks.length) { box.innerHTML = '<p class="empty">No dependency map loaded.</p>'; return; }
  box.innerHTML = risks.slice(0, 6).map((r) => `
    <div class="finding">
      <div class="head">
        <span class="pill ${r.Risk}">${r.Risk} risk</span>
        <span class="pill inferred">inferred</span>
        <span class="title" style="font-family:var(--mono);font-size:12.5px">${r.Service}</span>
      </div>
      <p>${r.Rationale}</p>
    </div>`).join('');
}

function renderGaps() {
  const box = el('gaps');
  const gaps = (state.assessment && state.assessment.CoverageGaps) || [];
  if (!gaps.length) { box.innerHTML = '<p class="empty">No coverage gaps recorded.</p>'; return; }
  box.innerHTML = gaps.map((g) => `
    <div class="metric-row" style="align-items:flex-start">
      <span class="label"><span class="pill low">${g.Kind}</span><br>${g.Detail}</span>
    </div>`).join('');
}

function renderRuns() {
  const tbody = el('runs');
  const runs = state.runs || [];
  if (!runs.length) { tbody.innerHTML = '<tr><td colspan="6" class="empty">No runs recorded.</td></tr>'; return; }
  tbody.innerHTML = runs.slice(0, 12).map((r) => {
    const integrity = r.divergenceDetected === true
      ? '<span style="color:var(--fail)">divergence</span>'
      : r.divergenceDetected === false ? '<span style="color:var(--ok)">consistent</span>'
      : '<span style="color:var(--text-faint)">not measured</span>';
    return `<tr>
      <td style="font-family:var(--mono);font-size:11px">${r.runId}</td>
      <td>${r.experiment}</td>
      <td style="color:var(--text-dim)">${r.rollbackReason || '—'}</td>
      <td>${r.recovered ? '<i class="dot s-ok"></i> yes' : '<i class="dot s-fail"></i> no'}</td>
      <td class="num">${r.recoverySeconds === null || r.recoverySeconds === undefined ? '—' : r.recoverySeconds + 's'}</td>
      <td>${integrity}</td>
    </tr>`;
  }).join('');
}

function renderExperiments() {
  const box = el('experiments');
  const running = state.run && state.run.isRunning;
  box.innerHTML = (state.experiments || []).map((e) => `
    <div class="exp-card" data-exp="${e.name}">
      <div class="top">
        <span class="exp-name">${e.name}</span>
        <span class="pill ${e.risk}">${e.risk} risk</span>
      </div>
      <dl>
        <dt>Target</dt><dd style="font-family:var(--mono)">${e.target}</dd>
        <dt>Blast radius</dt><dd>${e.expectedBlastRadius}</dd>
        <dt>Recovery</dt><dd>${e.recoveryPlan}</dd>
      </dl>
      <div class="controls">
        <label style="font-size:12px;color:var(--text-faint)">Duration
          <input type="number" min="${e.minDuration}" max="${e.maxDuration}" value="${e.defaultDuration}" id="dur-${e.name}">
        </label>
        ${e.supportsIntensity ? `<label style="font-size:12px;color:var(--text-faint)">${e.intensityLabel} ${e.intensityUnit}
          <input type="number" min="${e.minIntensity}" max="${e.maxIntensity}" value="${e.defaultIntensity}" id="intensity-${e.name}">
        </label>` : ''}
        <button class="danger" data-run="${e.name}" ${running ? 'disabled' : ''}>Approve &amp; inject</button>
        <button data-preflight="${e.name}">Preflight</button>
      </div>
      <div class="blockers" id="blockers-${e.name}"></div>
    </div>`).join('');

  box.querySelectorAll('[data-preflight]').forEach((b) =>
    b.addEventListener('click', () => preflight(b.dataset.preflight)));
  box.querySelectorAll('[data-run]').forEach((b) =>
    b.addEventListener('click', () => approveAndRun(b.dataset.run)));
}

async function preflight(name) {
  const box = el(`blockers-${name}`);
  box.innerHTML = 'Checking…';
  try {
    const result = await api(`/api/preflight/${encodeURIComponent(name)}`);
    box.innerHTML = result.ok
      ? '<span style="color:var(--ok)">Preflight passed — a clean baseline exists.</span>'
      : `Blocked:<ul>${result.blockers.map((b) => `<li>${b}</li>`).join('')}</ul>`;
    return result.ok;
  } catch (err) {
    box.innerHTML = `Preflight failed: ${err.message}`;
    return false;
  }
}

async function approveAndRun(name) {
  const exp = state.experiments.find((e) => e.name === name);
  const duration = Number(el(`dur-${name}`).value);
  // Only sent for an experiment the registry says takes one; the server
  // rejects a delay on anything else.
  const intensityInput = exp.supportsIntensity ? el(`intensity-${name}`) : null;
  const intensity = intensityInput ? Number(intensityInput.value) : undefined;
  const ok = await preflight(name);
  if (!ok) return;

  // Explicit, informed approval. The backend rejects anything without it, so
  // this is a real gate rather than a courtesy dialog.
  const confirmed = window.confirm(
    `Approve fault injection?\n\n` +
    `Experiment: ${name}
Target: ${exp.target}
Duration: ${duration}s` +
    (intensity ? `
${exp.intensityLabel}: ${intensity}${exp.intensityUnit}` : '') +
    `
Risk: ${exp.risk}

` +
    `Expected blast radius:\n${exp.expectedBlastRadius}\n\n` +
    `Recovery:\n${exp.recoveryPlan}\n\n` +
    `This will disrupt the sandbox. Continue?`);
  if (!confirmed) return;

  currentTarget = exp.target;
  try {
    await api('/api/experiments/run', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        experiment: name,
        durationSeconds: duration,
        intensity,
        approved: true,
        approvedBy: 'dashboard operator',
      }),
    });
    el(`blockers-${name}`).innerHTML = '<span style="color:var(--accent)">Approved. Injecting…</span>';
  } catch (err) {
    el(`blockers-${name}`).innerHTML = `Rejected: ${err.message}`;
    currentTarget = null;
  }
}

function renderKillSwitch() {
  const pill = el('stop-state');
  const engaged = state.killSwitchEngaged;
  pill.textContent = engaged ? 'STOP file present' : 'armed, not engaged';
  pill.className = `pill ${engaged ? 'high' : 'low'}`;
  el('btn-clear').disabled = !engaged;
}

el('btn-stop').addEventListener('click', async () => {
  await api('/api/experiments/stop', { method: 'POST' });
  refreshNow();
});
el('btn-clear').addEventListener('click', async () => {
  await api('/api/experiments/clear-stop', { method: 'POST' });
  refreshNow();
});
async function refreshNow() { state = await api('/api/state'); lastGoodFetch = new Date(); render(); }

// Timeline phases are derived from the experiment's own log lines, so the UI
// follows what the script actually did rather than a guessed schedule.
function renderTimeline() {
  const run = state.run || {};
  const log = run.log || [];
  const text = log.join('\n');
  const phases = [
    { key: 'preflight', label: 'Preflight & baseline', done: /baseline /.test(text) },
    { key: 'approval', label: 'Operator approval', done: !!run.startedUtc },
    { key: 'fault', label: 'Fault injected', done: /fault /.test(text) },
    { key: 'impact', label: 'Impact observed', done: /fault .*healthy=False|fault write .*accepted=False/.test(text) },
    { key: 'rollback', label: 'Rollback / kill switch', done: /Rollback:|recovery /.test(text) },
    { key: 'recovery', label: 'Recovery verified', done: /recovered\. Rollback|divergence check/i.test(text) },
  ];
  const firstPending = phases.findIndex((p) => !p.done);
  el('timeline').innerHTML = phases.map((p, i) => {
    const cls = p.done ? 'done' : (run.isRunning && i === firstPending ? 'active' : 'pending');
    return `<li class="${cls}">${p.label}</li>`;
  }).join('');

  const logBox = el('log');
  if (!log.length) {
    logBox.textContent = run.experiment
      ? `Last run: ${run.experiment} — exit code ${run.exitCode}`
      : 'No experiment has been started from this dashboard yet.';
  } else {
    logBox.textContent = log.slice(-60).join('\n');
    logBox.scrollTop = logBox.scrollHeight;
  }
  if (!run.isRunning && run.exitCode !== null && run.exitCode !== undefined) currentTarget = null;
}

refresh();

// Panels with their own data sources. Polled far less often than state:
// deliverable files and provider configuration do not change every 2s.
refreshProviderStatus();
renderSubmission();
setInterval(() => { refreshProviderStatus(); renderSubmission(); }, 60000);
