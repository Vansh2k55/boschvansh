# Grok API setup (Windows / PowerShell)

Everything in this project works without a Grok key. The deterministic agent,
the dashboard, the experiments and the kill switch are unaffected. A key only
adds two things: the AI experiment planner and the AI explanation of a run.

## 1. Get a key

1. Go to **https://console.x.ai/** and sign in.
2. Create an API key (API Keys section).
3. You may need to add credits or a billing method before requests succeed —
   xAI's free allowance changes, so check the console's billing page. A
   `401`/`403` usually means the key is wrong; a `429` usually means quota.

**Never** paste the key into chat, source code, a commit, a screenshot, or a
terminal screenshare. Nothing in this repo writes it to disk or to a log.

## 2. Set it for the session that starts the dashboard

The dashboard reads `XAI_API_KEY` from its own process environment, so set it in
the **same PowerShell window** you then start the dashboard from:

```powershell
$env:XAI_API_KEY = (Read-Host "Paste your xAI key" -AsSecureString |
    ForEach-Object { [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($_)) })
```

`Read-Host -AsSecureString` means the key is not echoed to the screen and does
not land in your PowerShell history. (Typing `$env:XAI_API_KEY = "sk-..."`
directly would put it in both — avoid that, especially on a shared screen.)

Then, from the repository root:

```powershell
dotnet run --project dashboard/Dashboard.csproj --urls http://localhost:5280
```

This lasts only for that window. Open a new terminal and it is gone, which is
what you want on a laptop you demo from.

### If you want it to persist

Only do this on a machine that is yours:

```powershell
[Environment]::SetEnvironmentVariable("XAI_API_KEY", $env:XAI_API_KEY, "User")
```

Restart your terminal afterwards. To remove it later:

```powershell
[Environment]::SetEnvironmentVariable("XAI_API_KEY", $null, "User")
```

## 3. Verify it worked, without printing the key

```powershell
# Is it set in this window? Prints only a length, never the value.
if ($env:XAI_API_KEY) { "XAI_API_KEY is set ($($env:XAI_API_KEY.Length) chars)" } else { "NOT set" }
```

With the dashboard running:

```powershell
(Invoke-WebRequest http://localhost:5280/api/ai/status -UseBasicParsing).Content
```

Expected: `{"configured":true,"model":"grok-4.7", ...}`. This endpoint reports
only whether a key exists — it never returns the key.

Then the real end-to-end check:

```powershell
(Invoke-WebRequest http://localhost:5280/api/ai/recommend -Method Post -UseBasicParsing).Content
```

A JSON recommendation with `"allowed":true` means Grok answered and the safety
layer accepted it.

## 4. Choosing a different model

Default is `grok-4.7`. To override:

```powershell
$env:XAI_MODEL = "grok-4.6"
```

Check **https://docs.x.ai/docs/models** for the current list before picking one.

## Running the dashboard in Docker (not required today)

The dashboard runs on the host, so this does not apply right now. If it is ever
containerised, pass the key at run time and never bake it into an image:

```powershell
docker run -e XAI_API_KEY=$env:XAI_API_KEY ...
```

Or with Compose, reference the variable without a value so it is read from the
environment rather than committed:

```yaml
environment:
  - XAI_API_KEY        # value comes from the host environment, never from the file
```

Never put the key in `docker-compose.yml`, a `Dockerfile`, or a committed `.env`.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Status says `configured:false` | Key set in a different window than the one running the dashboard. Set it and restart the dashboard in the same window. |
| `401` or `403` | Key is wrong, revoked, or has no access. Regenerate in the console. |
| `429` | Rate limit or no credits. Check billing at console.x.ai. |
| `Could not reach the Grok API` | No network, or a proxy/firewall blocking `api.x.ai`. |
| `Grok returned content that did not match the expected shape` | The model ignored the schema. Retry; if it persists, check the model name is current. |
| Buttons greyed out | The dashboard saw no key at startup. Restart it with the variable set. |

In every one of these cases the rest of the dashboard keeps working — the
deterministic score, the experiments and the kill switch do not depend on Grok.

## What to send me if something fails

Safe to share: the output of `/api/ai/status`, the HTTP status code, the error
text the dashboard displays, `dotnet build` output, and test results.

**Never share the key itself.** If an error message happens to contain it,
redact it before pasting.
