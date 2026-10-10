"""Opt-in Docker recovery audit. Never accepts an existing target/container.

Uses the checked-in cleanup functions and finally block, not a full Pitstop run.
HTTP serves a synthetic payload. No application/database recovery is implied.
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
LABEL = "pitstop.disposable-recovery-audit"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--execute-disposable", action="store_true")
    parser.add_argument("--pwsh", default="pwsh")
    args = parser.parse_args()
    if not args.execute_disposable:
        parser.error("Requires explicit --execute-disposable; never runs in unit tests")
    run = uuid.uuid4().hex
    containers = []
    result = {}

    def docker(*argv, check=True):
        p = subprocess.run(["docker", *argv], capture_output=True, text=True, timeout=30)
        if check and p.returncode:
            raise RuntimeError(p.stderr.strip() or p.stdout.strip())
        return p.stdout.strip()

    def owned(cid):
        info = json.loads(docker("inspect", cid))[0]
        assert info["Config"]["Labels"].get(LABEL) == run, "Refusing non-audit target"
        return info

    def remove(cid):
        if json.loads(docker("inspect", cid, check=False) or "[]"):
            owned(cid)
            docker("rm", "-f", cid)

    def create(*argv):
        cid = docker("run", "-d", "--label", f"{LABEL}={run}", *argv)
        containers.append(cid)
        owned(cid)
        return cid

    def tc(cid, *argv):
        owned(cid)
        return docker("run", "--rm", "--pull=never", "--label", f"{LABEL}={run}",
                      "--network", f"container:{cid}", "--cap-add", "NET_ADMIN",
                      "pitstop-netem:1.0", *argv)

    def qdisc(cid):
        return tc(cid, "qdisc", "show", "dev", "eth0")

    source = (ROOT / "chaos-scripts/network-delay.ps1").read_text(encoding="utf-8")
    functions = "\n".join(re.search(r"^function " + name + r" \{.*?^\}", source,
                                   re.M | re.S)[0]
                          for name in ("Invoke-Docker", "Invoke-Tc", "Get-Qdisc", "Remove-Shaping"))
    cleanup = re.search(r"^finally \{(.*?)^\}", source, re.M | re.S)[1]
    # Fail loudly if the production detached watchdog changes: this audit must
    # be updated rather than silently testing a different implementation.
    watchdog_command = "sleep 75; tc qdisc del dev eth0 root handle 28: 2>/dev/null || true"
    assert 'sleep $($DurationSeconds + 15); tc qdisc del dev eth0 root handle $handle 2>/dev/null || true' in source
    before = {x["Id"]: x["State"]["StartedAt"] for x in
              json.loads(docker("inspect", *docker("ps", "-q").split()))}
    try:
        target = create("--pull=never", "-p", "127.0.0.1::8080", "--entrypoint", "sh",
                        "pitstop-netem:1.0", "-c",
                        'while true; do printf "HTTP/1.1 200 OK\\r\\nContent-Length: 16\\r\\nConnection: close\\r\\n\\r\\nsynthetic-audit\\n" | nc -l -p 8080; done')
        port = owned(target)["NetworkSettings"]["Ports"]["8080/tcp"][0]["HostPort"]
        url = f"http://127.0.0.1:{port}/"

        def http_latency():
            start = time.monotonic()
            with urllib.request.urlopen(url, timeout=3) as response:
                assert response.read().strip() == b"synthetic-audit"
            return round((time.monotonic() - start) * 1000, 1)

        result["baseline"] = {"qdisc": qdisc(target), "http_ms": http_latency()}
        for fail in (False, True):
            watchdog = create("--rm", "--pull=never", "--network", f"container:{target}",
                              "--cap-add", "NET_ADMIN", "--entrypoint", "sh",
                              "pitstop-netem:1.0", "-c", watchdog_command)
            tc(target, "qdisc", "add", "dev", "eth0", "root", "handle", "28:", "netem", "delay", "50ms")
            shaped = qdisc(target)
            assert "netem 28:" in shaped and "delay 50ms" in shaped
            fault_latency = http_latency()
            prelude = (f"$container='{target}'; $watchdog='{watchdog}'; $netImage='pitstop-netem:1.0'; "
                       "$handle='28:'; $DurationSeconds=60; $injected=$true; $restored=$false; "
                       "$removalVerified=$false; $lock=$null; $failure=$null;\n")
            # Controlled primary-removal exception; watchdog has real NET_ADMIN.
            injected_failure = "\nfunction Remove-Shaping { throw 'audit primary cleanup failure' }\n" if fail else ""
            script = prelude + functions + injected_failure + "\ntry {} finally {" + cleanup + "}\n"
            with tempfile.TemporaryDirectory(prefix="pitstop-recovery-") as tmp:
                path = Path(tmp) / "cleanup.ps1"
                path.write_text(script, encoding="utf-8")
                p = subprocess.run([args.pwsh, "-NoProfile", "-File", str(path)],
                                   capture_output=True, text=True, timeout=35)
            assert p.returncode == 0, p.stderr
            immediate = qdisc(target)
            if fail:
                assert "netem" in immediate
                assert owned(watchdog)["State"]["Running"]
                assert watchdog in p.stdout, "Operator must receive full watchdog ID"
                deadline = time.monotonic() + 90
                while "netem" in qdisc(target) and time.monotonic() < deadline:
                    time.sleep(1)
            else:
                assert not json.loads(docker("inspect", watchdog, check=False) or "[]")
            after = qdisc(target)
            assert "netem" not in after, after
            result["failed_primary_cleanup" if fail else "successful_cleanup"] = {
                "fault_qdisc": shaped, "fault_http_ms": fault_latency,
                "immediate_qdisc": immediate, "final_qdisc": after,
                "recovery_http_ms": http_latency(), "operator_output": p.stdout.strip()}

        # Exercise the EXACT job bodies of both stop scripts, with disposable
        # Compose ownership. Kill only the pwsh process we start, not a user shell.
        for name, service in (("kill-service", "workshopmanagementapi"), ("kill-rabbitmq", "rabbitmq")):
            with tempfile.TemporaryDirectory(prefix="pitstop-job-") as tmp:
                compose = Path(tmp) / "compose.yml"
                compose.write_text(f"services:\n  {service}:\n    image: pitstop-netem:1.0\n    entrypoint: [sh, -c, 'sleep 300']\n    labels:\n      {LABEL}: {run}\n", encoding="utf-8")
                project = "audit-" + uuid.uuid4().hex[:10]
                docker("compose", "-p", project, "-f", str(compose), "up", "-d", "--pull", "never")
                cid = docker("compose", "-p", project, "-f", str(compose), "ps", "-q", service)
                containers.append(cid)
                owned(cid)
                text = (ROOT / f"chaos-scripts/{name}.ps1").read_text(encoding="utf-8")
                # The Start-Job watchdogs were replaced with detached containers after
                # this audit proved a job does not survive its parent. Extract the new
                # call and let it run through the real ChaosSafety module.
                job = re.search(r"^\s*\$watchdog = Start-RestartWatchdog .*$", text, re.M)[0]
                # Keep Compose project explicit through environment inherited by job.
                env = dict(os.environ, COMPOSE_PROJECT_NAME=project)
                for mode in ("forced_parent_termination", "session_exit"):
                    docker("start", cid)
                    module = (ROOT / "chaos-scripts/ChaosSafety.psm1").as_posix()
                    script = (f"$container='{cid}'; $composeFile='{compose}'; $service='{service}'; "
                              "$DurationSeconds=5; $DowntimeSeconds=5;\n"
                              f"Import-Module '{module}' -Force\n" + job +
                              f"\n& docker stop -t 0 {cid} | Out-Null\n" +
                              ("Start-Sleep -Seconds 30" if mode == "forced_parent_termination" else "exit 0"))
                    path = Path(tmp) / "parent.ps1"
                    path.write_text(script, encoding="utf-8")
                    parent = subprocess.Popen([args.pwsh, "-NoProfile", "-File", str(path)], env=env,
                                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    try:
                        deadline = time.monotonic() + 15
                        while owned(cid)["State"]["Running"] and time.monotonic() < deadline:
                            time.sleep(.2)
                        assert not owned(cid)["State"]["Running"], "No fault observed"
                        if mode == "forced_parent_termination":
                            parent.kill()
                        parent.wait(timeout=10)
                        # Wait to the watchdog's documented deadline, not a fixed 8s. The old
                        # Start-Job fired at $duration; the detached container fires at
                        # $duration + 15s grace, so a short window measures the clock, not the
                        # watchdog.
                        restart_deadline = time.monotonic() + 5 + 15 + 10
                        while not owned(cid)["State"]["Running"] and time.monotonic() < restart_deadline:
                            time.sleep(.5)
                        result[f"{name}_{mode}"] = {"restarted_by_watchdog": owned(cid)["State"]["Running"]}
                    finally:
                        if parent.poll() is None:
                            parent.kill()
                            parent.wait(timeout=10)
                        docker("start", cid)
                docker("compose", "-p", project, "-f", str(compose), "down", "--timeout", "1")
    finally:
        for cid in reversed(containers):
            remove(cid)
        remaining = docker("ps", "-aq", "--filter", f"label={LABEL}={run}")
        assert not remaining, remaining
        after = {x["Id"]: x["State"]["StartedAt"] for x in
                 json.loads(docker("inspect", *before))}
        assert before == after, "Pre-existing containers restarted during audit"
    result["resources_remaining"] = 0
    result["pre_existing_containers_unchanged"] = len(before)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
