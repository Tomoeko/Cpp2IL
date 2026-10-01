"""Bounded writer controls for validation descendants in separate sessions."""

import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

import process_ownership
import run_fixture
import storage_budget


WRITER = """
import os,pathlib,signal,sys,time
signal.signal(signal.SIGTERM, signal.SIG_IGN)
with open(sys.argv[2], 'ab', buffering=0) as output:
    marker = pathlib.Path(sys.argv[1])
    pending = marker.with_name(marker.name + '.pending')
    pending.write_text(str(os.getpid()))
    pending.replace(marker)
    until = time.monotonic() + 8
    while time.monotonic() < until:
        output.write(b'x')
        time.sleep(0.01)
"""
LEADER = """
import os,pathlib,signal,subprocess,sys,time
signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))
environment = os.environ.copy()
detached = len(sys.argv) < 6 or sys.argv[5] != 'stripped'
if not detached: environment.pop('CPP2IL_VALIDATION_PROCESS_SCOPES', None)
subprocess.Popen([sys.executable,'-c',sys.argv[1],sys.argv[2],sys.argv[3]],
                 env=environment, start_new_session=detached)
while not pathlib.Path(sys.argv[2]).exists(): time.sleep(0.01)
if sys.argv[4] != 'exit': time.sleep(8)
"""
MONITOR = """
import os,pathlib,sys
import run_fixture
run_fixture.ROOT = pathlib.Path(sys.argv[1])
command = [sys.executable, '-c', sys.argv[2], *sys.argv[3:]]
run_fixture.run_process(command, os.environ.copy(), run_fixture.ROOT/'Files'/'inner.log', 6)
"""


@unittest.skipUnless(sys.platform == 'darwin' or sys.platform.startswith('linux'),
                     'Native ownership reader requires macOS or Linux')
class ProcessOwnershipTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.files = self.root / 'Files'
        self.files.mkdir()
        self.marker = self.files / 'writer-pid'
        self.output = self.files / 'writer-output'
        self.addCleanup(self.stop_writer)

    def stop_writer(self):
        if self.marker.exists():
            try:
                os.kill(int(self.marker.read_text()), signal.SIGKILL)
            except ProcessLookupError:
                pass

    def environment(self):
        environment = os.environ.copy()
        paths = [str(Path(process_ownership.__file__).resolve().parent),
                 str(Path(run_fixture.storage_budget.__file__).resolve().parent)]
        environment['PYTHONPATH'] = os.pathsep.join(paths)
        return environment

    def sample(self, path):
        # Reach a synthetic limit only after the small writer has actually started.
        counted = storage_budget.LIMIT_BYTES if self.marker.exists() else 1
        return {'logicalBytes': counted, 'allocatedBytes': counted,
                'countedBytes': counted, 'limitBytes': storage_budget.LIMIT_BYTES}

    def check_stopped(self):
        self.assertTrue(self.marker.exists(), 'The controlled writer never started')
        time.sleep(0.05)
        size = self.output.stat().st_size
        time.sleep(0.15)
        self.assertEqual(self.output.stat().st_size, size, 'An owned descendant continued writing')

    def run_leader(self, fast_exit=False, monitors=0, timeout=5, budget=True, stripped=False):
        command = [sys.executable, '-c', LEADER, WRITER, str(self.marker), str(self.output),
                   'exit' if fast_exit else 'wait']
        if stripped:
            command.append('stripped')
        for _ in range(monitors):
            command = [sys.executable, '-c', MONITOR, str(self.root), *command[2:]]
        with mock.patch.object(run_fixture, 'ROOT', self.root), \
                mock.patch.object(storage_budget, 'POLL_SECONDS', timeout if fast_exit else 0.02), \
                mock.patch.object(storage_budget, 'usage', side_effect=self.sample if budget else
                                  lambda _: {'logicalBytes': 1, 'allocatedBytes': 1, 'countedBytes': 1,
                                             'limitBytes': storage_budget.LIMIT_BYTES}):
            result = run_fixture.run_process(command, self.environment(), self.files/'outer.log', timeout)
        self.assertFalse(run_fixture.process_succeeded(result))
        self.assertEqual(result['storageLimitReached'], budget)
        self.assertEqual(result['timedOut'], not budget)
        self.check_stopped()
        return result

    def test_budget_stop_kills_detached_writer_after_leader_exits_zero(self):
        self.assertEqual(self.run_leader()['exitCode'], 0)

    def test_existing_group_fallback_kills_writer_with_stripped_environment(self):
        self.assertEqual(self.run_leader(stripped=True)['exitCode'], 0)

    def test_backend_failure_still_kills_known_group_and_reaps_leader(self):
        self.check_backend_failure(RuntimeError('ownership backend failed'))

    def test_backend_timeout_is_not_mistaken_for_child_wait_timeout(self):
        self.check_backend_failure(subprocess.TimeoutExpired('ownership probe', 0.01))

    def check_backend_failure(self, failure):
        command = [sys.executable, '-c', LEADER, WRITER, str(self.marker), str(self.output), 'wait', 'stripped']
        original_launch = subprocess.Popen
        launched = []

        def capture_child(*args, **kwargs):
            process = original_launch(*args, **kwargs)
            launched.append(process)
            return process

        with mock.patch.object(run_fixture, 'ROOT', self.root), \
                mock.patch.object(storage_budget, 'POLL_SECONDS', 0.02), \
                mock.patch.object(storage_budget, 'usage', side_effect=self.sample), \
                mock.patch.object(process_ownership, 'signal_members', side_effect=failure) as signaler, \
                mock.patch.object(process_ownership, 'kill_members', side_effect=failure) as killer, \
                mock.patch.object(subprocess, 'Popen', side_effect=capture_child):
            with self.assertRaises(type(failure)) as raised:
                run_fixture.run_process(command, self.environment(), self.files/'backend.log', 5)
        self.assertIs(raised.exception, failure)
        self.assertEqual(signaler.call_count, 1)
        self.assertEqual(killer.call_count, 1)
        self.assertEqual(len(launched), 1)
        self.assertIsNotNone(launched[0].returncode, 'The direct leader was not reaped')
        self.check_stopped()

    def test_final_budget_sample_kills_detached_writer_after_natural_zero_exit(self):
        self.assertEqual(self.run_leader(fast_exit=True)['exitCode'], 0)

    def test_budget_stop_reaches_three_nested_new_sessions(self):
        self.run_leader(monitors=2)

    def test_timeout_reaches_nested_new_sessions(self):
        self.run_leader(monitors=1, timeout=0.8, budget=False)

    def test_unrelated_writer_survives_owned_scope_cleanup(self):
        other_pid, other_output = self.files/'unrelated-pid', self.files/'unrelated-output'
        environment = self.environment()
        environment.pop(process_ownership.SCOPE_ENVIRONMENT, None)
        unrelated = subprocess.Popen([sys.executable, '-c', WRITER, str(other_pid), str(other_output)],
                                     env=environment, start_new_session=True)
        try:
            self.run_leader(monitors=1)
            before = other_output.stat().st_size
            time.sleep(0.15)
            self.assertGreater(other_output.stat().st_size, before)
            self.assertIsNone(unrelated.poll())
        finally:
            unrelated.kill()
            unrelated.wait()

    def test_inner_scope_does_not_signal_parent_or_sibling_scope(self):
        outer, inner, sibling = 'a'*32, 'b'*32, 'c'*32
        processes = []
        try:
            for name, chain in (('parent', outer), ('inner', outer+':'+inner),
                                ('sibling', outer+':'+sibling)):
                environment = self.environment()
                environment[process_ownership.SCOPE_ENVIRONMENT] = chain
                processes.append(subprocess.Popen([sys.executable, '-c', 'import time;time.sleep(8)'],
                                                  env=environment, start_new_session=True))
            deadline = time.monotonic()+3
            while processes[1].pid not in process_ownership.members(inner):
                if time.monotonic() > deadline:
                    self.fail('Native scope reader did not observe the inner child')
                time.sleep(0.01)
            process_ownership.kill_members(inner)
            processes[1].wait(timeout=3)
            self.assertIsNone(processes[0].poll())
            self.assertIsNone(processes[2].poll())
        finally:
            for process in processes:
                if process.poll() is None:
                    process.kill()
                process.wait()

    def test_normal_success_keeps_actual_result_flags(self):
        with mock.patch.object(run_fixture, 'ROOT', self.root):
            result = run_fixture.run_process([sys.executable, '-c', 'pass'], self.environment(),
                                             self.files/'normal.log', 3)
        self.assertTrue(run_fixture.process_succeeded(result))
        self.assertEqual(result['exitCode'], 0)
        self.assertFalse(result['timedOut'])
        self.assertFalse(result['storageLimitReached'])

    def test_scope_chain_is_inherited_from_monitor_and_does_not_mutate_environment(self):
        parent = 'd'*32
        supplied = {process_ownership.SCOPE_ENVIRONMENT: 'untrusted', 'OTHER': 'value'}
        with mock.patch.dict(os.environ, {process_ownership.SCOPE_ENVIRONMENT: parent}):
            environment, scope = process_ownership.child_environment(supplied)
        self.assertEqual(environment[process_ownership.SCOPE_ENVIRONMENT], parent+':'+scope)
        self.assertRegex(scope, r'^[0-9a-f]{32}$')
        self.assertEqual(supplied[process_ownership.SCOPE_ENVIRONMENT], 'untrusted')

    def test_scope_lookalike_in_arguments_is_not_process_ownership(self):
        scope = 'e'*32
        environment = self.environment()
        environment.pop(process_ownership.SCOPE_ENVIRONMENT, None)
        process = subprocess.Popen([sys.executable, '-c', 'import time;time.sleep(8)',
                                    process_ownership.SCOPE_ENVIRONMENT+'='+scope],
                                   env=environment, start_new_session=True)
        try:
            self.assertNotIn(process.pid, process_ownership.members(scope))
            process_ownership.kill_members(scope)
            self.assertIsNone(process.poll())
        finally:
            process.kill()
            process.wait()

    def test_malformed_environment_entry_cannot_match_a_scope(self):
        if sys.platform != 'darwin':
            self.skipTest('Controlled native buffer case uses the macOS reader')
        scope = 'f'*32
        malformed = process_ownership.SCOPE_ENVIRONMENT.encode()+b'='+scope[:8].encode()+b'\xff'+scope[8:].encode()+b'\0'
        with mock.patch.object(process_ownership, '_darwin_environment', return_value=malformed):
            self.assertEqual(process_ownership._scope_chain(1), [])

    def test_malformed_inherited_scope_is_rejected(self):
        with mock.patch.dict(os.environ, {process_ownership.SCOPE_ENVIRONMENT: 'malformed'}):
            with self.assertRaises(ValueError):
                process_ownership.child_environment({})


if __name__ == '__main__':
    unittest.main()
