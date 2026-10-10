"""Opt-in broker rollback smoke test. No Pitstop endpoint, data, or container used."""
import argparse
import json
import subprocess
import time
import uuid

p = argparse.ArgumentParser()
p.add_argument('--execute-disposable', action='store_true')
args = p.parse_args()
if not args.execute_disposable:
    p.error('Requires --execute-disposable')
label = 'pitstop.disposable-broker-audit'
run = uuid.uuid4().hex
cid = None

def docker(*args, check=True):
    result = subprocess.run(['docker', *args], capture_output=True, text=True, timeout=20)
    if check and result.returncode:
        raise RuntimeError(result.stderr or result.stdout)
    return result

def ready():
    deadline = time.monotonic() + 100
    while time.monotonic() < deadline:
        node = docker('exec', cid, 'rabbitmq-diagnostics', '-q', 'check_running', check=False)
        ports = docker('exec', cid, 'rabbitmq-diagnostics', '-q', 'check_port_connectivity', check=False)
        if node.returncode == ports.returncode == 0:
            return round(time.monotonic() - (deadline - 100), 2)
        time.sleep(2)
    logs = docker('logs', '--tail', '20', cid, check=False)
    raise RuntimeError('Broker readiness deadline exceeded: ' + logs.stdout + logs.stderr)

def roundtrip():
    docker('exec', cid, 'rabbitmqadmin', 'declare', 'queue', 'name=audit', 'durable=false')
    docker('exec', cid, 'rabbitmqadmin', 'publish', 'routing_key=audit', 'payload=synthetic-audit')
    message = docker('exec', cid, 'rabbitmqadmin', 'get', 'queue=audit', 'ackmode=ack_requeue_false').stdout
    assert 'synthetic-audit' in message, message

try:
    # tmpfs keeps broker data disposable and avoids anonymous volumes entirely.
    cid = docker('run', '-d', '--pull=never', '--label', f'{label}={run}',
                 '--user', '100:101', '--tmpfs', '/var/lib/rabbitmq:uid=100,gid=101,mode=0700', '--env',
                 'RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS=+S 2:2 +SDcpu 1 +SDio 1',
                 '--env', 'RABBITMQ_CTL_ERL_ARGS=+S 1:1 +SDcpu 1 +SDio 1',
                 'rabbitmq:3-management-alpine').stdout.strip()
    baseline = ready()
    roundtrip()
    docker('kill', '-s', 'SIGKILL', cid)
    docker('stop', '-t', '1', cid)
    assert json.loads(docker('inspect', cid).stdout)[0]['State']['Running'] is False
    docker('start', cid)
    recovery = ready()
    roundtrip()
    print(json.dumps({'baseline_readiness_seconds': baseline,
                      'restart_readiness_seconds': recovery,
                      'baseline_and_recovery_publish_get': True,
                      'application_recovery_verified': False,
                      'prior_event_durability_verified': False}))
finally:
    if cid:
        info = json.loads(docker('inspect', cid).stdout)[0]
        assert info['Config']['Labels'].get(label) == run
        docker('rm', '-f', cid)
    assert not docker('ps', '-aq', '--filter', f'label={label}={run}').stdout.strip()
    print('Disposable broker containers remaining: 0')
