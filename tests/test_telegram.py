from contextlib import closing
from pathlib import Path
import sqlite3
import time
import unittest
from urllib.parse import urlparse, parse_qs
from support import Server
from fake_telegram import FakeTelegram, TOKEN


class TelegramTests(unittest.TestCase):
    def setUp(self):
        self.fake = FakeTelegram()
        self.addCleanup(self.fake.close)
        self.server = Server(Telegram__BotToken=TOKEN,Telegram__ApiBase=self.fake.base,
                             Telegram__ManualMode='true',Testing__UtcNow='2026-11-11T15:00:00Z').start()
        self.addCleanup(self.server.close)
        self.client = self.server.client()
        self.client.register('telegram@example.test')
        self.client.login('telegram@example.test')
        self.wait_ready()

    def wait_ready(self):
        for _ in range(50):
            if self.client.request('notifications/settings')[1]['ready']:
                return
            time.sleep(.1)
        self.fail('Подмена Telegram не подключилась')

    def code(self, client=None):
        code,data = (client or self.client).request('notifications/code','POST')
        self.assertEqual(code,200,data)
        return parse_qs(urlparse(data['url']).query)['start'][0]

    def poll(self):
        result = self.server.command('--telegram-poll')
        self.assertEqual(result.returncode,0,result.stderr)

    def test_binding_code_is_single_use(self):
        code = self.code()
        self.fake.update(code)
        self.poll()
        self.assertTrue(self.client.request('notifications/settings')[1]['connected'])
        self.fake.update(code,chat=20000000002)
        self.poll()
        with closing(sqlite3.connect(Path(self.server.temp.name)/'tracker.db')) as db:
            self.assertEqual(db.execute('SELECT ChatId FROM TelegramConnections').fetchone()[0],10000000001)
            self.assertEqual(db.execute('SELECT COUNT(*) FROM TelegramLinkCodes').fetchone()[0],0)

    def test_binding_code_expires_after_ten_minutes(self):
        code = self.code()
        with closing(sqlite3.connect(Path(self.server.temp.name)/'tracker.db')) as db:
            self.assertNotIn(code,str(db.execute('SELECT Hash FROM TelegramLinkCodes').fetchall()))
        self.server.stop()
        self.server.env['Testing__UtcNow'] = '2026-11-11T15:10:00Z'
        self.server.start()
        self.fake.update(code)
        self.poll()
        self.assertFalse(self.client.request('notifications/settings')[1]['connected'])

    def test_code_works_at_nine_minutes_fifty_nine_seconds(self):
        code = self.code()
        self.server.stop()
        self.server.env['Testing__UtcNow'] = '2026-11-11T15:09:59Z'
        self.server.start()
        self.fake.update(code)
        self.poll()
        self.wait_ready()
        self.assertTrue(self.client.request('notifications/settings')[1]['connected'])

    def test_new_code_invalidates_old_code(self):
        old,new = self.code(),self.code()
        self.fake.update(old)
        self.poll()
        self.assertFalse(self.client.request('notifications/settings')[1]['connected'])
        self.fake.update(new)
        self.poll()
        self.assertTrue(self.client.request('notifications/settings')[1]['connected'])

    def test_group_chat_cannot_bind(self):
        self.fake.update(self.code(),kind='group')
        self.poll()
        self.assertFalse(self.client.request('notifications/settings')[1]['connected'])
        self.assertEqual(self.fake.messages,[])

    def test_chat_bound_to_another_user_is_not_reassigned(self):
        self.fake.update(self.code())
        self.poll()
        other = self.server.client()
        other.register('second-telegram@example.test')
        other.login('second-telegram@example.test')
        self.fake.update(self.code(other))
        self.poll()
        self.assertFalse(other.request('notifications/settings')[1]['connected'])
        self.assertTrue(self.client.request('notifications/settings')[1]['connected'])

    def test_duplicate_update_is_ignored(self):
        self.fake.update(self.code())
        self.poll()
        self.poll()
        self.assertEqual(len(self.fake.messages),1)

    def test_disconnect_clears_pending_code(self):
        code = self.code()
        self.assertEqual(self.client.request('notifications/connection','DELETE')[0],204)
        self.fake.update(code)
        self.poll()
        self.assertFalse(self.client.request('notifications/settings')[1]['connected'])

    def test_code_issue_limit(self):
        for _ in range(5):
            self.code()
        self.assertEqual(self.client.request('notifications/code','POST')[0],429)

    def test_telegram_token_is_not_logged(self):
        self.fake.update(self.code())
        self.poll()
        result = self.server.command('--telegram-poll')
        self.assertNotIn(TOKEN,(result.stdout+result.stderr).decode('utf-8',errors='replace'))
        self.server.stop()


if __name__ == '__main__':
    unittest.main(verbosity=2)
