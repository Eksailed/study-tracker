from contextlib import closing
from pathlib import Path
import sqlite3
import unittest
from telegram_support import TelegramFixture


class SenderTests(TelegramFixture,unittest.TestCase):
    def prepare(self):
        self.bind()
        self.settings()
        self.task()
        self.advance('2026-11-11T16:00:00Z')

    def tick(self):
        result = self.server.command('--notifications-tick')
        self.assertEqual(result.returncode,0,result.stderr)

    def test_successful_send_records_message_id(self):
        self.prepare()
        self.tick()
        self.assertEqual(self.rows()[0]['State'],2)
        self.assertIsNotNone(self.rows()[0]['MessageId'])
        self.tick()
        self.assertEqual(len(self.fake.messages),1)

    def test_429_respects_retry_after(self):
        self.prepare()
        self.fake.responses.append({'ok':False,'error_code':429,'parameters':{'retry_after':5}})
        self.tick()
        self.assertEqual(self.rows()[0]['State'],0)
        self.tick()
        self.assertEqual(len(self.fake.messages),1)
        self.advance('2026-11-11T16:00:05Z')
        self.tick()
        self.assertEqual(self.rows()[0]['State'],2)
        self.assertEqual(len(self.fake.messages),2)

    def test_retry_limit_is_three(self):
        self.prepare()
        self.fake.responses.extend([{'ok':False,'error_code':429,'parameters':{'retry_after':1}}]*4)
        for second in range(4):
            if second:
                self.advance(f'2026-11-11T16:00:0{second}Z')
            self.tick()
        self.assertEqual(self.rows()[0]['State'],3)
        self.assertEqual(len(self.fake.messages),4)

    def test_403_disables_delivery(self):
        self.prepare()
        self.fake.responses.append({'ok':False,'error_code':403})
        self.tick()
        settings = self.client.request('notifications/settings')[1]
        self.assertFalse(settings['enabled'])
        self.assertIsNotNone(settings['connectionProblem'])
        self.assertEqual(self.rows()[0]['State'],3)

    def test_network_failure_is_unknown_without_automatic_retry(self):
        self.prepare()
        self.fake.responses.append('disconnect')
        self.tick()
        self.assertEqual(self.rows()[0]['State'],4)
        self.advance('2026-11-11T16:00:05Z')
        self.tick()
        self.assertEqual(len(self.fake.messages),1)

    def test_500_is_unknown_without_automatic_retry(self):
        self.prepare()
        self.fake.responses.append({'ok':False,'error_code':500})
        self.tick()
        self.assertEqual(self.rows()[0]['State'],4)
        self.tick()
        self.assertEqual(len(self.fake.messages),1)

    def test_restart_marks_inflight_send_unknown(self):
        self.prepare()
        self.plan()
        with closing(sqlite3.connect(Path(self.server.temp.name)/'tracker.db')) as db, db:
            db.execute('UPDATE ReminderDeliveries SET State=1, Attempts=1')
        self.tick()
        self.assertEqual(self.rows()[0]['State'],4)
        self.assertEqual(self.fake.messages,[])

    def test_reminder_contains_only_allowed_fields(self):
        self.prepare()
        self.tick()
        text = self.fake.messages[0]['text']
        self.assertIn('Название задания',text)
        self.assertIn('Предмет',text)
        self.assertIn('12 ноября 2026',text)
        self.assertNotIn('Не передавать описание',text)
        self.assertNotIn('telegram@example.test',text)
        self.assertNotIn('parse_mode',self.fake.messages[0])

    def test_400_is_failed_without_retry(self):
        self.prepare()
        self.fake.responses.append({'ok':False,'error_code':400})
        self.tick()
        self.assertEqual(self.rows()[0]['State'],3)
        self.tick()
        self.assertEqual(len(self.fake.messages),1)


if __name__ == '__main__':
    unittest.main(verbosity=2)
