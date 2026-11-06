"""Проверки настоящего HTTP API с отдельной временной базой SQLite."""
import http.cookiejar
import json
import os
from pathlib import Path
import shutil
import socket
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
                   DOTNET_ENVIRONMENT='Production')
        cls.process = subprocess.Popen([DOTNET, str(ROOT / 'bin/Debug/net10.0/StudyTracker.dll')],
                                       cwd=ROOT, env=env, stdout=cls.log, stderr=cls.log)
        cls.client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        for _ in range(100):
            try:
                with cls.client.open(cls.base + '/api/token', timeout=1) as response:
                    cls.token = json.load(response)['token']
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

    def test_task_lifecycle_and_subject_protection(self):
        subject = self.subject('Базы данных')
        task = self.task(subject)
        for status in ('InProgress', 'Done', 'Todo'):
            task['status'] = status
            code, task = self.request(f'assignments/{task["id"]}', 'PUT', task)
            self.assertEqual(code, 200)
            self.assertEqual(task['status'], status)
        self.assertEqual(self.request(f'subjects/{subject}', 'DELETE')[0], 409)
        self.assertEqual(self.request(f'assignments/{task["id"]}', 'DELETE')[0], 204)
        self.assertEqual(self.request(f'assignments/{task["id"]}', 'DELETE')[0], 404)
        self.assertEqual(self.request(f'subjects/{subject}', 'DELETE')[0], 204)

    def test_validation_rejects_invalid_assignments(self):
        subject = self.subject('Математика')
        valid = dict(title='Задачи', subjectId=subject, dueDate='2026-11-15', status='Todo')
        for invalid in ({'title': '  '}, {'title': 'a'*141}, {'description': 'a'*2001},
                        {'subjectId': 999999}, {'dueDate': '1999-01-01'}, {'dueDate': '2101-01-01'},
                        {'status': 99}, {'dueDate': 'ошибка'}, {'status': 'Unknown'}):
            with self.subTest(invalid=str(invalid)[:70]):
                self.assertEqual(self.request('assignments', 'POST', valid | invalid)[0], 400)

    def test_subject_validation_and_cyrillic_duplicates(self):
        self.subject('Программирование')
        self.assertEqual(self.request('subjects', 'POST', {'name': ' программирование ', 'color': 'teal'})[0], 409)
        for data in ({'name': '', 'color': 'blue'}, {'name': 'x'*81, 'color': 'blue'},
                     {'name': 'Химия', 'color': 'red'}, {'name': None, 'color': 'blue'}):
            self.assertEqual(self.request('subjects', 'POST', data)[0], 400)

    def test_csrf_required_for_all_mutations(self):
        for path, method, data in [('subjects', 'POST', {'name': 'Тест', 'color': 'blue'}),
                                   ('assignments/1', 'PUT', {}), ('subjects/1', 'DELETE', None)]:
            self.assertEqual(self.request(path, method, data, token=False)[0], 400)

    def test_data_survives_restart(self):
        subject = self.subject('Физика')
        task = self.task(subject, status='InProgress', description='Сохранить после перезапуска')
        self.stop_server()
        self.start_server()
        code, tasks = self.request('assignments')
        self.assertEqual(code, 200)
        stored = next(a for a in tasks if a['id'] == task['id'])
        self.assertEqual(stored['status'], 'InProgress')
        self.assertEqual(stored['description'], 'Сохранить после перезапуска')

    def test_missing_records_return_404(self):
        self.assertEqual(self.request('assignments/999999', 'DELETE')[0], 404)
        self.assertEqual(self.request('subjects/999999', 'DELETE')[0], 404)
        self.assertEqual(self.request('assignments/999999', 'PUT', {})[0], 404)

    def test_static_page_and_security_headers(self):
        with self.client.open(self.base) as response:
            self.assertEqual(response.status, 200)
            self.assertIn('Зачёт', response.read().decode('utf-8'))
            self.assertEqual(response.headers['X-Content-Type-Options'], 'nosniff')
            self.assertIn("frame-ancestors 'none'", response.headers['Content-Security-Policy'])
        with self.client.open(self.base + '/app.js') as response:
            self.assertEqual(response.status, 200)
        with self.client.open(self.base + '/api/subjects') as response:
            self.assertEqual(response.headers['Cache-Control'], 'no-store')


if __name__ == '__main__':
    unittest.main(verbosity=2)
