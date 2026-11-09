"""Проверки настоящего HTTP API с отдельной временной базой SQLite."""
import http.cookiejar
import json
import os
from pathlib import Path
import shutil
import socket
import sqlite3
import hashlib
from contextlib import closing
from support import Client
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
LOCAL_DOTNET = Path.home() / '.dotnet' / 'dotnet.exe'
DOTNET = os.environ.get('TEST_DOTNET') or (str(LOCAL_DOTNET) if LOCAL_DOTNET.exists() else shutil.which('dotnet'))


class TrackerApiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix='study-tracker-test-')
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            cls.port = sock.getsockname()[1]
        cls.base = f'http://127.0.0.1:{cls.port}'
        cls.start_server()

    @classmethod
    def start_server(cls):
        cls.log = tempfile.TemporaryFile()
        env = dict(os.environ, ASPNETCORE_URLS=cls.base, Tracker__DataDirectory=cls.temp.name,
                   DOTNET_ENVIRONMENT='Development', Telegram__BotToken='')
        migration = subprocess.run([DOTNET, str(ROOT / 'bin/Debug/net10.0/StudyTracker.dll'), '--migrate'],
                                   cwd=ROOT, env=env, capture_output=True, timeout=30)
        if migration.returncode:
            raise RuntimeError(migration.stderr.decode('utf-8', errors='replace'))
        cls.process = subprocess.Popen([DOTNET, str(ROOT / 'bin/Debug/net10.0/StudyTracker.dll')],
                                       cwd=ROOT, env=env, stdout=cls.log, stderr=cls.log)
        cls.client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        for _ in range(100):
            try:
                with cls.client.open(cls.base + '/api/token', timeout=1) as response:
                    cls.token = json.load(response)['token']
                session = Client(cls.base)
                session.register('api@example.test')
                if session.login('api@example.test')[0] != 200:
                    raise RuntimeError('Не удалось войти тестовым пользователем')
                cls.client = session.opener
                cls.token = session.token
                return
            except (OSError, urllib.error.URLError):
                if cls.process.poll() is not None:
                    break
                time.sleep(.1)
        cls.log.seek(0)
        output = cls.log.read().decode('utf-8', errors='replace')
        cls.stop_server()
        raise RuntimeError(output)

    @classmethod
    def stop_server(cls):
        cls.process.terminate()
        cls.process.wait(timeout=10)
        cls.log.close()

    @classmethod
    def tearDownClass(cls):
        cls.stop_server()
        cls.temp.cleanup()

    def request(self, path, method='GET', data=None, token=True):
        headers = {'Content-Type': 'application/json'}
        if token:
            headers['X-CSRF-TOKEN'] = self.token
        request = urllib.request.Request(self.base + '/api/' + path, method=method, headers=headers,
                                         data=None if data is None else json.dumps(data).encode())
        try:
            response = self.client.open(request, timeout=5)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            body = response.read()
            return response.status, json.loads(body) if body else None

    def subject(self, name):
        status, body = self.request('subjects', 'POST', {'name': name, 'color': 'blue'})
        self.assertEqual(status, 201, body)
        return body['id']

    def task(self, subject_id, **overrides):
        data = dict(title='Лабораторная № 2', description='Проверить запросы', subjectId=subject_id,
                    dueDate='2026-11-12', status='Todo')
        data.update(overrides)
        status, body = self.request('assignments', 'POST', data)
        self.assertEqual(status, 201, body)
        return body

    def run_migrations(self, directory):
        return subprocess.run([DOTNET, str(ROOT / 'bin/Debug/net10.0/StudyTracker.dll'), '--migrate'],
                              cwd=ROOT, env=dict(os.environ, Tracker__DataDirectory=directory),
                              capture_output=True, timeout=30)

    def test_migrations_create_empty_database(self):
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(self.run_migrations(directory).returncode, 0)
            with closing(sqlite3.connect(Path(directory) / 'tracker.db')) as db, db:
                self.assertEqual(db.execute('SELECT COUNT(*) FROM Subjects').fetchone()[0], 0)
                self.assertEqual(db.execute('SELECT COUNT(*) FROM Assignments').fetchone()[0], 0)
                self.assertGreater(db.execute('SELECT COUNT(*) FROM __EFMigrationsHistory').fetchone()[0], 0)

    def test_migrations_are_repeatable(self):
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(self.run_migrations(directory).returncode, 0)
            with closing(sqlite3.connect(Path(directory) / 'tracker.db')) as db, db:
                db.execute("INSERT INTO AspNetUsers (Id, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount) VALUES ('test',0,0,0,0,0)")
                db.execute("INSERT INTO Subjects (Name, NameKey, Color, OwnerId) VALUES ('Test', 'TEST', 'blue', 'test')")
            self.assertEqual(self.run_migrations(directory).returncode, 0)
            with closing(sqlite3.connect(Path(directory) / 'tracker.db')) as db, db:
                self.assertEqual(db.execute('SELECT Name FROM Subjects').fetchone()[0], 'Test')

    def test_legacy_database_is_not_modified(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'tracker.db'
            with closing(sqlite3.connect(path)) as db, db:
                db.execute('CREATE TABLE Subjects (Id INTEGER PRIMARY KEY, Name TEXT)')
                db.execute("INSERT INTO Subjects (Name) VALUES ('Old')")
            before = hashlib.sha256(path.read_bytes()).hexdigest()
            self.assertEqual(self.run_migrations(directory).returncode, 1)
            self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), before)

    def test_shared_migrated_database_is_not_modified(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'tracker.db'
            with closing(sqlite3.connect(path)) as db, db:
                db.executescript('''CREATE TABLE Subjects (Id INTEGER PRIMARY KEY,Name TEXT,NameKey TEXT,Color TEXT);
                    CREATE TABLE __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY,ProductVersion TEXT);''')
                db.execute("INSERT INTO Subjects VALUES (1,'Общий предмет','ОБЩИЙ ПРЕДМЕТ','blue')")
                db.execute("INSERT INTO __EFMigrationsHistory VALUES ('old_schema','10.0.14')")
            before = hashlib.sha256(path.read_bytes()).hexdigest()
            self.assertEqual(self.run_migrations(directory).returncode, 1)
            self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), before)


if __name__ == '__main__':
    unittest.main(verbosity=2)
