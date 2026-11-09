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

    def test_timeout_is_unknown_without_automatic_retry(self):
        self.prepare()
        self.fake.responses.append('timeout')
        self.tick()
        self.assertEqual(self.rows()[0]['State'],4)
        self.tick()
        self.assertEqual(len(self.fake.messages),1)

    def test_truncated_response_is_unknown_without_automatic_retry(self):
        self.prepare()
        self.fake.responses.append('truncated')
        self.tick()
        self.assertEqual(self.rows()[0]['State'],4)
        self.tick()
        self.assertEqual(len(self.fake.messages),1)


if __name__ == '__main__':
    unittest.main(verbosity=2)
