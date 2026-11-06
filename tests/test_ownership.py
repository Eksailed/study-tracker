from contextlib import closing
import hashlib
from pathlib import Path
import sqlite3
import tempfile
import unittest
from support import Server


class OwnershipTests(unittest.TestCase):
    def setUp(self):
        self.server = Server().start()
        self.addCleanup(self.server.close)
        self.a, self.b = self.server.client(), self.server.client()
        for client, email in ((self.a, 'a@example.test'), (self.b, 'b@example.test')):
            self.assertEqual(client.register(email)[0], 201)
            self.assertEqual(client.login(email)[0], 200)

    def subject(self, client, name='Математика'):
        code, data = client.request('subjects', 'POST', dict(name=name, color='blue'))
        self.assertEqual(code, 201, data)
        return data['id']

    def task(self, client, subject):
        return client.request('assignments', 'POST', dict(title='Контрольная', description='Подготовиться',
                              subjectId=subject, dueDate='2026-12-10', status='InProgress'))

    def test_anonymous_cannot_access_assignments(self):
        anon = self.server.client()
        for path, method, data in [('assignments', 'GET', None), ('subjects', 'GET', None),
                                  ('assignments', 'POST', {}), ('assignments/1', 'PUT', {}),
                                  ('assignments/1', 'DELETE', None), ('subjects/1', 'DELETE', None)]:
            with self.subTest(path=path, method=method):
                self.assertEqual(anon.request(path, method, data)[0], 401)

    def test_users_see_only_own_records(self):
        subject = self.subject(self.a)
        self.assertEqual(self.task(self.a, subject)[0], 201)
        self.assertEqual(self.b.request('subjects')[1], [])
        self.assertEqual(self.b.request('assignments')[1], [])
        self.assertEqual(len(self.a.request('assignments')[1]), 1)

    def test_foreign_assignment_cannot_be_changed_or_deleted(self):
        subject = self.subject(self.a)
        task = self.task(self.a, subject)[1]
        self.assertEqual(self.b.request(f'assignments/{task["id"]}', 'PUT', task | {'title':'Чужое'})[0], 404)
        self.assertEqual(self.b.request(f'assignments/{task["id"]}', 'DELETE')[0], 404)
        self.assertEqual(self.b.request(f'subjects/{subject}', 'DELETE')[0], 404)
        self.assertEqual(self.a.request('assignments')[1][0]['title'], 'Контрольная')

    def test_foreign_subject_cannot_be_used(self):
        subject_a, subject_b = self.subject(self.a), self.subject(self.b)
        self.assertEqual(self.task(self.b, subject_a)[0], 404)
        task = self.task(self.b, subject_b)[1]
        self.assertEqual(self.b.request(f'assignments/{task["id"]}', 'PUT', task | {'subjectId':subject_a})[0], 404)
        with closing(sqlite3.connect(Path(self.server.temp.name)/'tracker.db')) as db, db:
            db.execute('PRAGMA foreign_keys=ON')
            with self.assertRaises(sqlite3.IntegrityError):
                db.execute('UPDATE Assignments SET SubjectId=? WHERE Id=?', (subject_a,task['id']))

    def test_same_subject_name_for_different_users(self):
        self.subject(self.a)
        self.subject(self.b)
        self.assertEqual(self.a.request('subjects', 'POST', dict(name='математика',color='teal'))[0], 409)

    def legacy_database(self, directory, invalid=False):
        path = Path(directory)/'old.db'
        with closing(sqlite3.connect(path)) as db, db:
            db.executescript('''CREATE TABLE Subjects(Id INTEGER PRIMARY KEY,Name TEXT,NameKey TEXT,Color TEXT);
                CREATE TABLE Assignments(Id INTEGER PRIMARY KEY,Title TEXT,Description TEXT,SubjectId INTEGER,
                DueDate TEXT,Status INTEGER,CreatedAt TEXT);''')
            db.execute("INSERT INTO Subjects VALUES (12,'Физика','ФИЗИКА','teal')")
            db.execute("INSERT INTO Assignments VALUES (23,'Опыт','Описание',12,'2026-12-12',?,'2026-11-06 10:00:00')", (99 if invalid else 1,))
        return path

    def test_legacy_import_preserves_records(self):
        with tempfile.TemporaryDirectory() as directory:
            source = self.legacy_database(directory)
            before = hashlib.sha256(source.read_bytes()).hexdigest()
            result = self.server.command('--import-legacy', str(source), '--owner', 'a@example.test')
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(hashlib.sha256(source.read_bytes()).hexdigest(), before)
            tasks = self.a.request('assignments')[1]
            self.assertEqual(len(tasks), 1)
            self.assertEqual(tasks[0]['description'], 'Описание')
            self.assertEqual(tasks[0]['dueDate'], '2026-12-12')
            self.assertEqual(tasks[0]['status'], 'InProgress')
            self.assertTrue(tasks[0]['createdAt'].startswith('2026-11-06T10:00:00'))
            self.assertEqual(self.b.request('assignments')[1], [])
            self.assertEqual(self.server.command('--import-legacy', str(source), '--owner', 'a@example.test').returncode, 1)
            self.assertEqual(len(self.a.request('assignments')[1]), 1)

    def test_legacy_import_rolls_back_invalid_data(self):
        with tempfile.TemporaryDirectory() as directory:
            source = self.legacy_database(directory, invalid=True)
            self.assertEqual(self.server.command('--import-legacy', str(source), '--owner', 'a@example.test').returncode, 1)
            self.assertEqual(self.a.request('subjects')[1], [])
            self.assertEqual(self.a.request('assignments')[1], [])

    def test_legacy_import_rejects_unknown_owner(self):
        with tempfile.TemporaryDirectory() as directory:
            source = self.legacy_database(directory)
            self.assertEqual(self.server.command('--import-legacy', str(source), '--owner', 'missing@example.test').returncode, 1)


if __name__ == '__main__':
    unittest.main(verbosity=2)
