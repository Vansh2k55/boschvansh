// AI advisory layer.
//
// Grok output is rendered deliberately differently from measured values: dashed
// purple borders and an "AI" pill everywhere, so nobody reads a model's opinion
// as a measurement. Approving a recommendation posts to the same endpoint as
// the manual path, with the same explicit approval flag — the server revalidates
// and trusts nothing because an AI suggested it.
//
// Shares the global scope of app.js (el, api, state, currentTarget).

let aiRecommendation = null;

async function refreshAiStatus() {
  try {
    const s = await api('/api/ai/status');
    const badge = el('ai-status');
    const active = s.activeProvider;
    badge.textContent = active
      ? `active: ${active}`
      : 'no AI provider available — deterministic analysis unaffected';
    badge.style.color = active ? 'var(--ok)' : 'var(--text-faint)';

    // Show every provider and why it is or is not usable, so a fallback is
    // never a mystery.
    el('provider-status').innerHTML = (s.providers || []).map((p) => {
      const cls = p.available ? 's-ok' : 's-unknown';
      return `<span title="${p.detail}"><i class="dot ${cls}"></i>
        <strong style="color:var(--text-dim)">${p.provider}</strong>
        <span style="font-family:var(--mono)">${p.model}</span></span>`;
    }).join('');

    el('btn-recommend').disabled = !active;
    el('btn-explain').disabled = !active;
  } catch {
    // Status is advisory. The rest of the page must not depend on it.
  }
}

el('btn-recommend').addEventListener('click', async () => {
  const box = el('ai-recommendation');
  const btn = el('btn-recommend');
  btn.disabled = true;
  box.innerHTML = '<p class="empty">Asking the AI provider, with the live evidence…</p>';
  try {
    const result = await api('/api/ai/recommend', { method: 'POST' });
    aiRecommendation = result;
    renderRecommendation(result);
  } catch (err) {
    box.innerHTML = `<div class="ai-reject"><strong>No AI provider could answer.</strong> ${err.message}
      <p style="margin:6px 0 0;color:var(--text-dim)">Everything else on this page still works — pick an experiment manually below.</p></div>`;
  } finally {
    btn.disabled = false;
  }
});

function renderRecommendation(result) {
  const box = el('ai-recommendation');
  const r = result.recommendation || {};

  // A rejection shows both what the model asked for and why it was refused.
  // That is the safety layer doing its job, and it is worth seeing.
  if (!result.allowed) {
    box.innerHTML = `
      <div class="ai-reject">
        <strong>Recommendation rejected by the safety layer.</strong>
        <ul>${(result.rejections || []).map((x) => `<li>${x}</li>`).join('')}</ul>
        <p style="margin:7px 0 0;color:var(--text-faint)">
          The model proposed <code>${r.experiment || '(none)'}</code>
          ${r.target ? `against <code>${r.target}</code>` : ''}
          ${r.durationSeconds ? `for ${r.durationSeconds}s` : ''}. Nothing was run.
        </p>
      </div>`;
    return;
  }

  const resolved = result.resolved;
  const pf = result.preflight || { ok: false, blockers: ['Preflight was not evaluated.'] };
  const signals = (r.expectedSignals || []).map((s) => `<li>${s}</li>`).join('');
  const pre = (r.preconditions || []).map((s) => `<li>${s}</li>`).join('');

  box.innerHTML = `
    <div class="ai-card">
      <div class="headline">
        <span class="exp">${resolved.experiment}</span>
        <span class="pill ai">${result.provider} proposed</span>
        <span class="pill validated">validated</span>
        <span class="pill ${r.risk}">${r.risk} risk</span>
      </div>
      <dl>
        <dt>Target</dt><dd style="font-family:var(--mono)">${resolved.target} <span style="color:var(--text-faint)">(fixed by catalog)</span></dd>
        <dt>Duration</dt><dd>${r.durationSeconds}s <span style="color:var(--text-faint)">(allowed ${resolved.minDuration}–${resolved.maxDuration}s)</span></dd>
        <dt>Hypothesis</dt><dd>${r.hypothesis}</dd>
        <dt>Expected impact</dt><dd>${r.expectedImpact}</dd>
        <dt>Why this one</dt><dd>${r.reasoning || '—'}</dd>
        <dt>Risk basis</dt><dd>${r.riskJustification}</dd>
        <dt>Success</dt><dd>${r.successCriteria}</dd>
        <dt>Failure</dt><dd>${r.failureCriteria}</dd>
        <dt>Recovery</dt><dd>${r.recoveryExpectation}</dd>
        <dt>Rollback</dt><dd>${r.rollbackStrategy}</dd>
      </dl>
      ${signals ? `<div style="font-size:12px;color:var(--text-faint)">Signals to watch:<ul>${signals}</ul></div>` : ''}
      ${pre ? `<div style="font-size:12px;color:var(--text-faint);margin-top:7px">Preconditions:<ul>${pre}</ul></div>` : ''}

      <div style="margin-top:11px;font-size:12.5px">
        ${pf.ok
          ? '<span style="color:var(--ok)">Preflight passed — safe to run now.</span>'
          : `<span style="color:var(--warn)">Preflight blocked:</span><ul style="margin:4px 0 0;padding-left:17px;color:var(--text-dim);font-size:12px">${pf.blockers.map((b) => `<li>${b}</li>`).join('')}</ul>`}
      </div>

      <div class="controls" style="margin-top:11px">
        <button class="danger" id="btn-ai-approve" ${pf.ok ? '' : 'disabled'}>Approve &amp; run this experiment</button>
        <button id="btn-ai-dismiss">Dismiss</button>
      </div>
    </div>`;

  el('btn-ai-dismiss').addEventListener('click', () => {
    box.innerHTML = '';
    aiRecommendation = null;
  });
  const approve = el('btn-ai-approve');
  if (approve) approve.addEventListener('click', runRecommended);
}

async function runRecommended() {
  if (!aiRecommendation) return;
  const r = aiRecommendation.recommendation;
  const resolved = aiRecommendation.resolved;

  const confirmed = window.confirm(
    'Approve the AI-recommended experiment?\n\n' +
    `Experiment: ${resolved.experiment}\nTarget: ${resolved.target}\n` +
    `Duration: ${r.durationSeconds}s\nRisk: ${r.risk}\n\n` +
    `Hypothesis:\n${r.hypothesis}\n\nExpected impact:\n${r.expectedImpact}\n\n` +
    `Rollback:\n${r.rollbackStrategy}\n\n` +
    `${aiRecommendation.provider} recommended this. You are authorising it. Continue?`);
  if (!confirmed) return;

  currentTarget = resolved.target;
  try {
    await api('/api/experiments/run', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        experiment: resolved.experiment,
        durationSeconds: r.durationSeconds,
        approved: true,
        approvedBy: 'dashboard operator (AI-recommended)',
      }),
    });
    el('ai-recommendation').innerHTML =
      '<div class="ai-card"><span style="color:var(--accent)">Approved and running — watch the timeline and live monitoring.</span></div>';
    aiRecommendation = null;
  } catch (err) {
    el('ai-recommendation').innerHTML +=
      `<div class="ai-reject"><strong>Rejected by the server.</strong> ${err.message}</div>`;
    currentTarget = null;
  }
}

el('btn-explain').addEventListener('click', async () => {
  const box = el('ai-explanation');
  const runs = (state && state.runs) || [];
  if (!runs.length) {
    box.innerHTML = '<p class="empty">No runs to explain yet.</p>';
    return;
  }
  const runId = runs[0].runId;
  el('explain-target').textContent = `run ${runId}`;
  box.innerHTML = '<p class="empty">Asking the AI provider to explain the evidence…</p>';
  try {
    const result = await api(`/api/ai/explain/${encodeURIComponent(runId)}`, { method: 'POST' });
    renderExplanation(result.explanation, runId, result.provider);
  } catch (err) {
    box.innerHTML = `<div class="ai-reject"><strong>No AI provider could answer.</strong> ${err.message}
      <p style="margin:6px 0 0;color:var(--text-dim)">The deterministic assessment above is unaffected.</p></div>`;
  }
});

function renderExplanation(e, runId, provider) {
  const box = el('ai-explanation');
  if (!e) {
    box.innerHTML = '<p class="empty">No explanation returned.</p>';
    return;
  }
  const list = (items) => (items || []).map((x) => `<li>${x}</li>`).join('');
  box.innerHTML = `
    <div class="ai-card">
      <div class="headline">
        <span class="pill ai">${provider || 'AI'} narrative</span>
        <span style="font-family:var(--mono);font-size:11px;color:var(--text-faint)">${runId}</span>
      </div>
      <p style="margin:0 0 9px">${e.summary || ''}</p>
      <dl>
        <dt>Root cause</dt><dd>${e.rootCause || '—'}</dd>
        <dt>Propagation</dt><dd>${e.failurePropagation || '—'}</dd>
        <dt>Expected vs actual</dt><dd>${e.expectedVsActual || '—'}</dd>
      </dl>
      ${(e.weaknesses || []).length ? `<div style="font-size:12px;color:var(--text-faint)">Weaknesses:<ul>${list(e.weaknesses)}</ul></div>` : ''}
      ${(e.recommendations || []).length ? `<div style="font-size:12px;color:var(--text-faint);margin-top:7px">Recommendations:<ul>${list(e.recommendations)}</ul></div>` : ''}
      ${(e.evidenceCited || []).length ? `<div class="evidence" style="margin-top:9px">${(e.evidenceCited || []).join(' · ')}</div>` : ''}
      ${(e.unsupported || []).length
        ? `<div class="ai-unsupported"><strong>Stated as unsupported by the evidence:</strong><ul>${list(e.unsupported)}</ul></div>`
        : ''}
    </div>`;
}

refreshAiStatus();
