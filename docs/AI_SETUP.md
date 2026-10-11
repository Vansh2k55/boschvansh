# AI provider setup (Windows / PowerShell)

The AI layer has two providers and degrades through them:

1. **Groq Cloud** — preferred when `GROQ_API_KEY` is set. Fast, better quality.
2. **Ollama (local)** — used when Groq is unconfigured, rate-limited, or down.
3. **Neither** — the dashboard keeps working on its deterministic assessment.

Everything in this project works with **no AI at all**. The resilience score,
the experiments, preflight, rollback and the kill switch do not depend on any
provider.

## Your machine, as inspected on 2026-10-10

Ollama **is already installed and working**: version 0.40.2, with three models:

| Model | Size | Notes |
| --- | --- | --- |
| `qwen3.5:4b` | 3.4 GB | Tools, thinking, 262k context. **Too slow here** — exceeded 240s |
| `deepseek-r1:8b` | 5.2 GB | Larger, slower, stronger reasoning |
| `qwen2.5:3b` | 1.9 GB | **Current default.** Smallest and fastest — 9–18s, measured |

**You do not need to install anything for the local fallback.** It was tested
end to end and produced a valid, validated recommendation.

Nothing in this project downloads a model. If you ever point it at a model you
do not have, it reports that and skips the provider rather than pulling GBs.

## Groq (optional, recommended for the demo)

Local inference on CPU takes roughly 30–90 seconds per recommendation. Groq
answers in a couple of seconds, which matters when a mentor is watching.

### 1. Get a key

Go to **https://console.groq.com/**, sign in, and create an API key. Groq has a
free tier; a `429` means rate limit or quota, not a bug — the dashboard will
simply fall back to Ollama.

**Never** paste the key into chat, source code, a commit, or a screenshot.

### 2. Set it in the window you start the dashboard from

```powershell
$env:GROQ_API_KEY = (Read-Host "Paste your Groq key" -AsSecureString |
    ForEach-Object { [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($_)) })
```

`-AsSecureString` keeps the key off the screen and out of your PowerShell
history. Typing `$env:GROQ_API_KEY = "gsk_..."` directly would put it in both.

Then, from the repository root:

```powershell
dotnet run --project dashboard/Dashboard.csproj --urls http://localhost:5280
```

The variable lives only in that window. To persist it on a machine that is
yours:

```powershell
[Environment]::SetEnvironmentVariable("GROQ_API_KEY", $env:GROQ_API_KEY, "User")
```

## Check what you have

```powershell
# Ollama
ollama --version
ollama list
Invoke-RestMethod http://localhost:11434/api/tags | Select-Object -ExpandProperty models | Select-Object name

# Groq key present in this window? Prints a length, never the value.
if ($env:GROQ_API_KEY) { "GROQ_API_KEY set ($($env:GROQ_API_KEY.Length) chars)" } else { "not set — Ollama will be used" }
```

With the dashboard running:

```powershell
(Invoke-WebRequest http://localhost:5280/api/ai/status -UseBasicParsing).Content
```

This reports each provider's availability, model and reason, plus which one is
active. It never returns the key.

## Configuration

`dashboard/appsettings.json`:

```json
"Ai": {
  "Groq":   { "Endpoint": "https://api.groq.com/openai/v1/chat/completions",
              "Model": "openai/gpt-oss-20b", "TimeoutSeconds": 60 },
  "Ollama": { "BaseUrl": "http://localhost:11434",
              "Model": "qwen2.5:3b", "TimeoutSeconds": 120 }
}
```

Environment overrides, for one session:

| Variable | Effect |
| --- | --- |
| `GROQ_API_KEY` | Enables Groq. **Environment only — never in a config file.** |
| `GROQ_MODEL` | Override the Groq model |
| `OLLAMA_MODEL` | e.g. `deepseek-r1:8b` for stronger local reasoning |
| `OLLAMA_BASE_URL` | If Ollama runs elsewhere |

### A warning about the Groq model

Groq only honours `response_format: json_schema` in strict mode on the
**gpt-oss family and `qwen/qwen3.8-27b`**. Switching `GROQ_MODEL` to a Llama
model would accept the request and **silently ignore the schema**, which means
unstructured output and far more rejections. Check
**https://console.groq.com/docs/structured-outputs** before changing it.

## Which local model should I use?

**`qwen2.5:3b` — the default, chosen by measurement rather than by size.**

`qwen3.5:4b` was the default until it was timed against the real schema and the
real evidence payload. It does not finish:

| Model | Time to a conforming answer | Result |
| --- | --- | --- |
| `qwen2.5:3b` | **18.4s cold, 9.1–9.5s warm** | Conforming, and deterministic at temperature 0 |
| `qwen3.5:4b` | **exceeded 240s** | Timed out in the dashboard; no recommendation at all |

Measured on 2026-10-11 against the live schema from `/api/ai/schema` and the
8.2KB evidence payload the dashboard actually sends, using the same request
shape as `OllamaClient` (POST `/api/chat`, `stream: false`, raw schema in
`format`, temperature 0). Three consecutive `qwen2.5:3b` runs each returned the
same valid recommendation.

That timeout was not hypothetical: a live dashboard recommendation returned
HTTP 503 with `ollama: Ollama timed out after 240s`, so the planner produced
nothing. Constrained decoding against a 15-property schema is the expensive
part, and a smaller model pays it far more cheaply than a larger one reasons
its way to a better answer.

The timeout is now 120s rather than 240s — about six times the measured cold
start, so a genuine hang still fails in a usable amount of time.

If you want a larger model, measure it before making it the default:

```powershell
$env:OLLAMA_MODEL = "deepseek-r1:8b"   # slower; time it first
```

Smaller models do hallucinate experiment names. That is handled — see below —
and is a cheaper problem than a provider that never answers.

## How a bad recommendation is handled

This is not theoretical. On the first real run, `qwen3.5:4b` proposed an
experiment called **`chaos-engine`** targeting **`pitstop`** — neither exists.
The server rejected it and nothing ran:

```
'chaos-engine' is not an allowlisted experiment. Permitted: kill-service, kill-rabbitmq.
```

Two layers handle this:

1. The response schema constrains `experiment` and `target` with **enums**, so a
   model is structurally pushed toward valid values.
2. `RecommendationValidator` checks every field again regardless of provider.
   The target comes from the catalog, never the model, and an out-of-range
   duration is **rejected, not clamped**.

The enum makes the common case work. The validator is the safety boundary, and
it applies identically to Groq and Ollama.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| Status shows no active provider | No Groq key and Ollama not running. Start Ollama, or set a key. |
| `GROQ_API_KEY` ignored | Set in a different window than the dashboard. Set it and restart the dashboard there. |
| Groq `401` / `403` | Key wrong or revoked. Regenerate at console.groq.com. |
| Groq `429` | Rate limit or quota. **It falls back to Ollama automatically.** |
| `'<model>' is not installed` | Install it (`ollama pull <model>`) or point `OLLAMA_MODEL` at one you have. |
| Ollama times out | Local inference on CPU is slow. Raise `TimeoutSeconds`, or use a smaller model. |
| Recommendations keep getting rejected | Small model hallucinating. Try `deepseek-r1:8b`, or use Groq. |

In every case the deterministic dashboard keeps working.

## What to send me if something fails

Safe: the output of `/api/ai/status`, the HTTP status, the error the dashboard
shows, `ollama list`, build and test output.

**Never the key.** If an error contains it, redact it first.
