from contextlib import closing
from pathlib import Path
import sqlite3
import unittest
from support import running_server


class AccountTests(unittest.TestCase):
    def test_registration_and_login(self):
        with running_server() as server:
            client = server.client()
            self.assertEqual(client.register('student@example.test')[0], 201)
            self.assertEqual(client.request('auth/me')[0], 401)
            self.assertEqual(client.login('student@example.test')[0], 200)
            self.assertEqual(client.request('auth/me')[1]['email'], 'student@example.test')
            cookie = next(c for c in client.jar if c.name == 'Zachet.Account')
            attributes = {key.lower(): value for key, value in cookie._rest.items()}
            self.assertIn('httponly', attributes)
            self.assertEqual(attributes['samesite'], 'lax')
            with closing(sqlite3.connect(Path(server.temp.name)/'tracker.db')) as db:
                password_hash = db.execute('SELECT PasswordHash FROM AspNetUsers').fetchone()[0]
            self.assertNotEqual(password_hash, 'a-long-test-password')
            self.assertGreater(len(password_hash), 50)

    def test_logout_ends_session(self):
        with running_server() as server:
            client = server.client()
            client.register('logout@example.test')
            client.login('logout@example.test')
            self.assertEqual(client.logout()[0], 204)
            self.assertEqual(client.request('auth/me')[0], 401)

    def test_short_password_is_rejected(self):
        with running_server() as server:
            client = server.client()
            self.assertEqual(client.register('short@example.test', 'a'*11)[0], 400)
            self.assertEqual(client.register('valid@example.test', 'a'*12)[0], 201)
            self.assertEqual(client.register('long@example.test', 'a'*129)[0], 400)

    def test_login_is_locked_after_five_failures(self):
        with running_server() as server:
            client = server.client()
            client.register('locked@example.test')
            for _ in range(5):
                self.assertEqual(client.login('locked@example.test','wrong-password')[0], 401)
            self.assertEqual(client.login('locked@example.test')[0], 401)
            with closing(sqlite3.connect(Path(server.temp.name)/'tracker.db')) as db:
                self.assertIsNotNone(db.execute('SELECT LockoutEnd FROM AspNetUsers').fetchone()[0])

    def test_auth_requires_csrf(self):
        with running_server() as server:
            client = server.client()
            for path in ('auth/register', 'auth/login'):
                self.assertEqual(client.request(path, 'POST', {}, token=False)[0], 400)
            client.register('csrf@example.test')
            client.login('csrf@example.test')
            self.assertEqual(client.request('auth/logout', 'POST', token=False)[0], 400)

    def test_registration_can_be_disabled(self):
        with running_server(Accounts__AllowRegistration='false') as server:
            client = server.client()
            self.assertFalse(client.request('auth/options')[1]['allowRegistration'])
            self.assertEqual(client.register('closed@example.test')[0], 403)

    def test_auth_rate_limit(self):
        with running_server() as server:
            client = server.client()
            for _ in range(10):
                self.assertEqual(client.login('missing@example.test')[0], 401)
            self.assertEqual(client.login('missing@example.test')[0], 429)

    def test_production_registration_is_closed_by_default(self):
        with running_server(DOTNET_ENVIRONMENT='Production') as server:
            self.assertEqual(server.client().register('prod@example.test')[0], 403)

    def test_login_rotates_csrf_identity(self):
        with running_server() as server:
            client = server.client()
            client.register('rotate@example.test')
            anonymous_token = client.token
            self.assertEqual(client.login('rotate@example.test')[0], 200)
            new_token = client.token
            client.token = anonymous_token
            self.assertEqual(client.request('auth/logout', 'POST')[0], 400)
            client.token = new_token
            self.assertEqual(client.logout()[0], 204)


if __name__ == '__main__':
    unittest.main(verbosity=2)
