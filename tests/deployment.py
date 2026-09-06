"""Exercise the real deployment script with a fake Docker daemon in a disposable container.

Run only with: docker run --rm --network none -v "$PWD:/repo:ro" \
    python:3.12-slim python /repo/tests/deployment.py
"""
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import unittest

if not Path('/.dockerenv').exists() or Path('/var/run/docker.sock').exists():
    raise SystemExit('Run inside a disposable container without a Docker socket.')

APP = Path('/var/lymdunbot')
STATE = Path('/tmp/fake-docker.json')
SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/deploy-production.sh'
REVISION = 'a' * 40

FAKE_DOCKER = r'''#!/usr/local/bin/python3
import hashlib, json, pathlib, sys
args = sys.argv[1:]
path = pathlib.Path('/tmp/fake-docker.json')
state = json.loads(path.read_text())
state['calls'].append(args)
def done(output='', code=0):
    path.write_text(json.dumps(state))
    print(output)
    sys.exit(code)
if args[0] == 'info' or args[:2] == ['compose', 'version']:
    done('OK')
if args[0] == 'compose':
    if 'ps' in args:
        done('bot')
    if 'up' in args:
        assert all(x in args for x in ['--no-deps', '--force-recreate', '--no-build'])
        assert args[-1] == 'lymdunette-bot'
        override = pathlib.Path('/var/lymdunbot/docker-compose.cd.yml').read_text()
        state['image'] = 'old-image' if 'lymdunettebot-rollback:' in override else 'new-image'
        state['activations'].append(state['image'])
        done()
if args[0] == 'inspect':
    if '--format' not in args:
        done('{}')
    fmt = args[args.index('--format') + 1]
    if '.Mounts' in fmt:
        done('/wrong/data' if state['scenario'] == 'bad-mount' else '/var/lymdunbot/data')
    if '.State.Running' in fmt:
        restarts = int(state['scenario'] == 'restarts' and state['image'] == 'new-image')
        done('true %s %s' % (restarts, state['image']))
    done(state['image'])
if args[0] == 'build':
    state['dll'] = hashlib.sha256((pathlib.Path(args[-1]) / 'LymdunetteBot.dll').read_bytes()).hexdigest()
    done(code=1 if state['scenario'] == 'build-fails' else 0)
if args[:2] == ['image', 'inspect']:
    done('new-image')
if args[0] == 'run':
    done(state['dll'] + '  /app/LymdunetteBot.dll' if 'sha256sum' in args else '')
if args[0] == 'tag':
    done()
if args[0] == 'logs':
    done('Starting...' if state['scenario'] == 'not-ready' and state['image'] == 'new-image'
         else 'SchedulerService Ready fired!')
done('Unexpected Docker call: ' + repr(args), 99)
'''


def archive(extra=None):
    data = io.BytesIO()
    with tarfile.open(fileobj=data, mode='w:gz') as tar:
        files = {'LymdunetteBot.dll': b'published-dll', 'Dockerfile': b'FROM scratch', '.dockerignore': b'.env\ndata'}
        files.update(extra or {})
        for name, content in files.items():
            member = tarfile.TarInfo(name)
            member.size = len(content)
            tar.addfile(member, io.BytesIO(content))
    return data.getvalue()


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        for path in (APP, Path('/var/lib/lymdunettebot-cd'), Path('/var/backups/lymdunbot')):
            if path.exists():
                shutil.rmtree(path)
        APP.mkdir(parents=True)
        (APP / 'data').mkdir()
        (APP / 'data/state.json').write_text('{"preserved": true}')
        (APP / '.env').write_text('DISCORD_TOKEN=test-only')
        (APP / 'docker-compose.yml').write_text('services:\n  lymdunette-bot: {}\n')
        STATE.write_text(json.dumps({'scenario': 'success', 'image': 'old-image', 'activations': [], 'calls': []}))

    def deploy(self, scenario='success', payload=None, checksum=None, command=None):
        state = json.loads(STATE.read_text())
        state['scenario'] = scenario
        STATE.write_text(json.dumps(state))
        payload = payload if payload is not None else archive()
        command = command or 'deploy %s %s' % (REVISION, checksum or hashlib.sha256(payload).hexdigest())
        result = subprocess.run(['bash', str(SCRIPT)], input=payload, capture_output=True,
                                env=dict(os.environ, SSH_ORIGINAL_COMMAND=command), timeout=20)
        self.assertEqual((APP / '.env').read_text(), 'DISCORD_TOKEN=test-only')
        self.assertEqual((APP / 'data/state.json').read_text(), '{"preserved": true}')
        return result, json.loads(STATE.read_text())

    def test_success_records_revision_and_retains_rollback(self):
        result, state = self.deploy()
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        self.assertEqual(state['activations'], ['new-image'])
        self.assertEqual(Path('/var/lib/lymdunettebot-cd/current-revision').read_text().strip(), REVISION)
        self.assertEqual(len(list(Path('/var/backups/lymdunbot').glob('*/rollback.yml'))), 1)

    def test_failed_activation_restores_previous_image(self):
        for scenario in ('not-ready', 'restarts', 'bad-mount'):
            with self.subTest(scenario=scenario):
                self.setUp()
                result, state = self.deploy(scenario)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(state['activations'], ['new-image', 'old-image'])
                self.assertIn(b'Previous release restored and ready.', result.stderr)
                self.assertFalse(Path('/var/lib/lymdunettebot-cd/current-revision').exists())

    def test_build_failure_does_not_replace_container(self):
        result, state = self.deploy('build-fails')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(state['activations'], [])

    def test_bad_checksum_does_not_build_or_activate(self):
        result, state = self.deploy(checksum='0' * 64)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any(call[0] == 'build' for call in state['calls']))
        self.assertEqual(state['activations'], [])

    def test_unsafe_archives_are_rejected(self):
        for name in ('../escape', '/tmp/escape', '.env', 'data/state.json'):
            with self.subTest(name=name):
                result, state = self.deploy(payload=archive({name: b'unsafe'}))
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(state['activations'], [])

    def test_arbitrary_ssh_commands_are_rejected(self):
        for command in ('bash', 'deploy ' + REVISION + ' $(id)', 'check; id'):
            result, state = self.deploy(command=command)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(state['activations'], [])

    def test_connection_check_is_read_only(self):
        result, state = self.deploy(command='check')
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        self.assertEqual(state['activations'], [])


if __name__ == '__main__':
    Path('/usr/bin/docker').write_text(FAKE_DOCKER)
    Path('/usr/bin/docker').chmod(0o755)
    Path('/usr/bin/sleep').write_text('#!/bin/sh\nexit 0\n')
    Path('/usr/bin/sleep').chmod(0o755)
    if not Path('/usr/bin/python3').exists():
        Path('/usr/bin/python3').symlink_to(sys.executable)
    unittest.main(verbosity=2)
