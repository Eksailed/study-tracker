"""Изолированный сервер, временная база и независимые браузерные сеансы."""
from contextlib import contextmanager
import http.cookiejar
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
LOCAL_DOTNET = Path.home() / '.dotnet' / 'dotnet.exe'
DOTNET = os.environ.get('TEST_DOTNET') or (str(LOCAL_DOTNET) if LOCAL_DOTNET.exists() else shutil.which('dotnet'))
COMMAND = [DOTNET, str(ROOT / 'bin/Debug/net10.0/StudyTracker.dll')]


class Client:
    def __init__(self, base, ssl_context=None):
        self.base = base
        self.jar = http.cookiejar.CookieJar()
        handlers = [urllib.request.HTTPCookieProcessor(self.jar)]
        if ssl_context is not None:
            handlers.append(urllib.request.HTTPSHandler(context=ssl_context))
        self.opener = urllib.request.build_opener(*handlers)
        self.token = None
        self.headers = {}
        self.refresh_token()

    def refresh_token(self):
        self.token = self.request('token')[1]['token']

    def request(self, path, method='GET', data=None, token=True):
        headers = {'Content-Type': 'application/json'}
        if token and self.token:
            headers['X-CSRF-TOKEN'] = self.token
        req = urllib.request.Request(self.base + '/api/' + path, method=method, headers=headers,
                                     data=None if data is None else json.dumps(data).encode())
        try:
            response = self.opener.open(req, timeout=5)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            self.headers = dict(response.headers)
            body = response.read()
            return response.status, json.loads(body) if body else None

    def register(self, email, password='a-long-test-password'):
        return self.request('auth/register', 'POST', dict(email=email, password=password))

    def login(self, email, password='a-long-test-password'):
        result = self.request('auth/login', 'POST', dict(email=email, password=password))
        self.refresh_token()
        return result

    def logout(self):
        result = self.request('auth/logout', 'POST')
        self.refresh_token()
        return result


class Server:
    def __init__(self, **settings):
        self.temp = tempfile.TemporaryDirectory(prefix='zachet-tests-')
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            self.base = f'http://127.0.0.1:{sock.getsockname()[1]}'
        self.env = dict(os.environ, ASPNETCORE_URLS=self.base, Tracker__DataDirectory=self.temp.name,
                        DOTNET_ENVIRONMENT='Development')
        self.env.update(settings)

    def start(self):
        migration = self.command('--migrate')
        if migration.returncode:
            raise RuntimeError(migration.stderr.decode('utf-8', errors='replace'))
        self.log = tempfile.TemporaryFile()
        self.process = subprocess.Popen(COMMAND, cwd=ROOT, env=self.env, stdout=self.log, stderr=self.log)
        for _ in range(100):
            try:
                with urllib.request.urlopen(self.base+'/api/token', timeout=1):
                    return self
            except OSError:
                if self.process.poll() is not None:
                    break
                time.sleep(.1)
        self.log.seek(0)
        raise RuntimeError(self.log.read().decode('utf-8', errors='replace'))

    def command(self, *args, input=None):
        return subprocess.run(COMMAND + list(args), cwd=ROOT, env=self.env, input=input, capture_output=True, timeout=30)

    def stop(self):
        if hasattr(self, 'process'):
            self.process.terminate()
            self.process.wait(timeout=10)
            self.log.close()
            del self.process

    def close(self):
        self.stop()
        self.temp.cleanup()

    def client(self):
        return Client(self.base)


@contextmanager
def running_server(**settings):
    server = Server(**settings)
    try:
        yield server.start()
    finally:
        server.close()
