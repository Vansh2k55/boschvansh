'use strict';

// Milestone status pills, the baseline/impact/recovery chart, and submission
// readiness. Kept out of app.js so neither file grows past the point where you
// can read it in one sitting.
//
// Every status word here is derived from live state or from the filesystem. The
// four permitted words mean exactly what they say:
//
//   Verified            - measured in this environment, evidence on screen
//   Complete            - present and substantial
//   Needs Configuration - the feature works but something is not set up
//   Incomplete          - genuinely not there yet
//
// Nothing is hardcoded to a happy value, because a dashboard that always says
// "Verified" tells the operator nothing.

const MS_CLASS = {
  Verified: 'ms-ok',
  Complete: 'ms-ok',
  'Needs Configuration': 'ms-warn',
  Incomplete: 'ms-bad',
};

function setMilestone(id, status, detail) {
  const node = document.getElementById(id);
  if (!node) return;
  node.className = `ms ${MS_CLASS[status] || 'ms-warn'}`;
  node.textContent = detail ? `${status} · ${detail}` : status;
}

// Provider status is fetched separately from /api/state, so it is cached here
// and refreshed on its own. Null means "not asked yet", which is not the same
// as "unavailable" and must not be rendered as a failure.
let providerSummary = null;

function renderMilestones() {
  if (!state) return;

  const services = state.services || [];
  const running = services.filter((s) => s.running).length;
  setMilestone(
    'ms-env',
    services.length > 0 && running === services.length ? 'Verified' : 'Incomplete',
    `${running}/${services.length} services running`,
  );

  const graph = state.graph;
  const nodeCount = graph && graph.nodes ? graph.nodes.length : 0;
  setMilestone(
    'ms-map',
    nodeCount > 0 ? 'Verified' : 'Needs Configuration',
    nodeCount > 0
      ? `${nodeCount} services · ${graph.edges.length} dependencies`
      : 'run map-dependencies.ps1',
  );

  const runs = state.runs || [];
  setMilestone(
    'ms-baseline',
    runs.length > 0 ? 'Verified' : 'Incomplete',
    runs.length > 0 ? `${runs.length} recorded runs` : 'no run to chart yet',
  );

  setMilestone(
    'ms-assessment',
    state.assessment ? 'Verified' : 'Incomplete',
    state.assessment ? 'deterministic scoring' : 'run analyze.ps1',
  );

  // Distinct experiment types actually exercised, which is what the brief
  // asks for - not the number of runs, which would flatter the result.
  const types = new Set(runs.map((r) => r.experiment).filter(Boolean));
  setMilestone(
    'ms-history',
    runs.length > 0 ? 'Verified' : 'Incomplete',
    `${runs.length} runs · ${types.size} failure types`,
  );

  const executable = (state.experiments || []).length;
  setMilestone(
    'ms-injection',
    executable > 0 ? 'Verified' : 'Incomplete',
    `${executable} executable · approval required`,
  );

  const recovered = runs.filter((r) => r.recovered).length;
  setMilestone(
    'ms-recovery',
    state.killSwitchEngaged ? 'Needs Configuration'
      : recovered > 0 ? 'Verified' : 'Incomplete',
    state.killSwitchEngaged
      ? 'STOP engaged — clear it to run'
      : `${recovered}/${runs.length} runs recovered`,
  );

  if (providerSummary) {
    setMilestone('ms-planner', providerSummary.status, providerSummary.detail);
  }
}

async function refreshProviderStatus() {
  try {
    const s = await api('/api/ai/status');
    const providers = s.providers || [];
    const up = providers.filter((p) => p.available);
    // The field is `provider`, not `name` - checked against the live payload
    // from /api/ai/status rather than assumed.
    providerSummary = up.length
      ? {
        status: 'Verified',
        detail: `${up.map((p) => p.provider).join(', ')} configured`
            + (s.activeProvider ? ` · trying ${s.activeProvider} first` : ''),
      }
      : { status: 'Needs Configuration', detail: 'no provider configured' };
  } catch (err) {
    // An unreachable status endpoint is not the same as an unconfigured
    // provider, and must not be reported as one.
    providerSummary = { status: 'Needs Configuration', detail: 'status unavailable' };
  }
  renderMilestones();
}

// ---------------------------------------------------------------------------
// Baseline / impact / recovery chart
// ---------------------------------------------------------------------------

const PHASES = ['baseline', 'fault', 'recovery'];
const SERIES_COLOURS = ['#4f9cf9', '#f2c14e', '#7ddf9a', '#d98cf2'];
let chartRunId = null;
let chartCache = {};

function populateChartRuns() {
  const select = document.getElementById('chart-run');
  if (!select || !state) return;
  const runs = state.runs || [];
  const wanted = runs.map((r) => r.runId).join('|');
  if (select.dataset.loaded === wanted) return;
  select.dataset.loaded = wanted;

  if (!runs.length) {
    select.innerHTML = '<option value="">no runs recorded</option>';
    return;
  }
  select.innerHTML = runs
    .slice(0, 20)
    .map((r) => `<option value="${r.runId}">${r.experiment || 'run'} · ${r.runId}</option>`)
    .join('');
  if (!chartRunId) {
    chartRunId = runs[0].runId;
    select.value = chartRunId;
    loadChart(chartRunId);
  }
}

async function loadChart(runId) {
  if (!runId) return;
  const box = document.getElementById('chart');
  if (!chartCache[runId]) {
    box.innerHTML = '<p class="empty">Loading samples&hellip;</p>';
    try {
      chartCache[runId] = await api(`/api/runs/${runId}`);
    } catch (err) {
      box.innerHTML = `<p class="empty">Could not load this run: ${err.message}</p>`;
      return;
    }
  }
  drawChart(chartCache[runId]);
}

function drawChart(report) {
  const box = document.getElementById('chart');
  const note = document.getElementById('chart-note');
  const legendBox = document.getElementById('chart-legend');
  const samples = (report && report.Samples) || [];

  if (!samples.length) {
    // An older report with no per-probe samples is a real condition, and
    // saying so is better than drawing an empty pair of axes.
    box.innerHTML = '<p class="empty">This run recorded no per-probe samples, so there is nothing to chart.</p>';
    note.textContent = '';
    legendBox.innerHTML = '';
    return;
  }

  const modules = [...new Set(samples.map((s) => s.Module))];
  const present = PHASES.filter((p) => samples.some((s) => s.Phase === p));

  // Ordered by phase then by original sequence, so the x axis reads
  // baseline -> fault -> recovery regardless of how the file is ordered.
  const ordered = [];
  present.forEach((phase) => {
    samples.forEach((s, i) => {
      if (s.Phase === phase) ordered.push({ ...s, seq: i });
    });
  });

  const W = 720;
  const H = 240;
  const padL = 46;
  const padR = 10;
  const padT = 18;
  const padB = 30;
  const plotW = W - padL - padR;
  const plotH = H - padT - padB;

  // Scaled to real measurements only. A failed probe's "latency" is just when
  // the client gave up - a timeout constant, not something the service did -
  // so letting it set the ceiling would squash every genuine reading into a
  // flat line at the bottom. Failures get their own row instead, at failY.
  const healthy = ordered.filter((s) => s.Healthy).map((s) => s.LatencyMs || 0);
  const maxLatency = Math.max(...healthy, 1);
  const slots = Math.max(ordered.length, 2);
  const x = (i) => padL + (plotW * i) / (slots - 1);
  const y = (ms) => padT + plotH - (plotH * Math.min(ms, maxLatency)) / maxLatency;
  const failY = padT + 7;

  // Phase bands, so impact is visible as a region rather than inferred.
  let bands = '';
  let cursor = 0;
  present.forEach((phase, pi) => {
    const count = ordered.filter((s) => s.Phase === phase).length;
    const x0 = x(cursor);
    const x1 = x(Math.min(cursor + count - 1, slots - 1));
    const width = Math.max(x1 - x0, 1);
    bands += `<rect x="${x0.toFixed(1)}" y="${padT}" width="${width.toFixed(1)}" height="${plotH}"
      class="band band-${phase}"></rect>
      <text x="${(x0 + width / 2).toFixed(1)}" y="${H - 9}" class="band-label">${phase}</text>`;
    cursor += count;
    if (pi < present.length - 1) {
      bands += `<line x1="${x1.toFixed(1)}" y1="${padT}" x2="${x1.toFixed(1)}" y2="${padT + plotH}" class="band-edge"></line>`;
    }
  });

  // One line per module. A failed probe breaks the line and is marked, rather
  // than being drawn at its timeout latency as though it were a measurement
  // of a working page.
  let lines = '';
  let marks = '';
  modules.forEach((mod, mi) => {
    const colour = SERIES_COLOURS[mi % SERIES_COLOURS.length];
    let run = [];
    const flush = () => {
      if (run.length > 1) {
        lines += `<polyline points="${run.join(' ')}" fill="none" stroke="${colour}" stroke-width="1.8"></polyline>`;
      } else if (run.length === 1) {
        const [px, py] = run[0].split(',');
        marks += `<circle cx="${px}" cy="${py}" r="2.2" fill="${colour}"></circle>`;
      }
      run = [];
    };
    ordered.forEach((s, i) => {
      if (s.Module !== mod) return;
      const px = x(i).toFixed(1);
      const py = y(s.LatencyMs || 0).toFixed(1);
      if (s.Healthy) {
        run.push(`${px},${py}`);
      } else {
        // Drawn on the failure row, not at its timeout value.
        flush();
        marks += `<path d="M${(+px - 3.2).toFixed(1)},${failY - 3.2} l6.4,6.4 M${(+px - 3.2).toFixed(1)},${failY + 3.2} l6.4,-6.4"
          stroke="${colour}" stroke-width="1.6" fill="none"></path>`;
      }
    });
    flush();
  });

  const ticks = [0, maxLatency / 2, maxLatency]
    .map((v) => `<line x1="${padL}" y1="${y(v).toFixed(1)}" x2="${W - padR}" y2="${y(v).toFixed(1)}" class="grid"></line>
      <text x="${padL - 6}" y="${(y(v) + 3.5).toFixed(1)}" class="axis">${Math.round(v)}</text>`)
    .join('');

  const failRow = marks
    ? `<line x1="${padL}" y1="${failY}" x2="${W - padR}" y2="${failY}" class="grid fail-row"></line>
       <text x="${padL - 6}" y="${failY + 3.5}" class="axis">failed</text>`
    : '';

  box.innerHTML = `<svg class="chart" viewBox="0 0 ${W} ${H}" role="img"
      aria-label="Module latency across baseline, fault and recovery phases">
      ${bands}${ticks}${failRow}${lines}${marks}
      <text x="4" y="12" class="axis">ms</text>
    </svg>`;

  legendBox.innerHTML = modules
    .map((m, i) => `<span><i class="dot" style="background:${SERIES_COLOURS[i % SERIES_COLOURS.length]}"></i>${m}</span>`)
    .join('') + '<span><i class="dot dot-x"></i>failed probe — not a latency measurement</span>';

  // Phase means, computed from the probes actually recorded. A phase with no
  // probes reads "not probed" rather than 0ms.
  const target = report.TargetService || '';
  // Three outcomes, kept distinct. "All N failed" is a measurement; "not
  // probed" is the absence of one. Collapsing them would report a module that
  // was hammered and never answered exactly like a module nobody looked at.
  const mean = (phase, mod) => {
    const all = samples.filter((s) => s.Phase === phase && s.Module === mod);
    if (!all.length) return 'not probed';
    const ok = all.filter((s) => s.Healthy).map((s) => s.LatencyMs);
    if (!ok.length) return `all ${all.length} probes failed`;
    const avg = Math.round(ok.reduce((a, b) => a + b, 0) / ok.length);
    return ok.length === all.length
      ? `${avg}ms`
      : `${avg}ms (${all.length - ok.length} of ${all.length} failed)`;
  };
  const focus = modules.find((m) => target.toLowerCase().includes(m.replace('Management', '').toLowerCase()))
    || modules[0];
  note.textContent = `${samples.length} probes · ${focus}: baseline ${mean('baseline', focus)}`
    + ` → fault ${mean('fault', focus)} → recovery ${mean('recovery', focus)}`
    + (report.Recovered ? ` · recovered in ${report.RecoverySeconds}s` : ' · did not recover');
}

// ---------------------------------------------------------------------------
// Submission readiness
// ---------------------------------------------------------------------------

async function renderSubmission() {
  const box = document.getElementById('submission');
  if (!box) return;
  let data;
  try {
    data = await api('/api/submission');
  } catch (err) {
    box.innerHTML = `<p class="empty">Could not read deliverable status: ${err.message}</p>`;
    return;
  }

  const items = data.deliverables || [];
  const done = items.filter((d) => d.status === 'Complete').length;

  box.innerHTML = `<table>
      <thead><tr><th>Deliverable</th><th>Status</th><th>Path</th></tr></thead>
      <tbody>${items.map((d) => `<tr>
        <td>${d.name}<div style="color:var(--text-faint);font-size:11px">${d.note}</div></td>
        <td><span class="ms ${MS_CLASS[d.status] || 'ms-bad'}">${d.status}</span></td>
        <td style="font-family:var(--mono);font-size:11px;color:var(--text-dim)">${d.path}</td>
      </tr>`).join('')}</tbody>
    </table>
    <p style="margin:10px 0 0;color:var(--text-faint);font-size:11.5px">${data.videoNote}</p>`;

  setMilestone('ms-submission', done === items.length ? 'Complete' : 'Incomplete',
    `${done}/${items.length} prepared`);
}

document.addEventListener('change', (e) => {
  if (e.target && e.target.id === 'chart-run') {
    chartRunId = e.target.value;
    loadChart(chartRunId);
  }
});
