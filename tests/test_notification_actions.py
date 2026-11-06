import unittest
from telegram_support import TelegramFixture


class NotificationActionTests(TelegramFixture,unittest.TestCase):
    def tick(self):
        result = self.server.command('--notifications-tick')
        self.assertEqual(result.returncode,0,result.stderr)

    def test_test_message_is_rate_limited(self):
        self.bind()
        self.assertEqual(self.client.request('notifications/test','POST')[0],202)
        self.assertEqual(self.client.request('notifications/test','POST')[0],429)
        self.advance('2026-11-11T15:01:00Z')
        self.assertEqual(self.client.request('notifications/test','POST')[0],202)

    def test_test_message_requires_explicit_request(self):
        self.bind()
        self.tick()
        self.assertEqual(self.fake.messages,[])
        self.assertEqual(self.client.request('notifications/test','POST')[0],202)
        self.tick()
        self.assertEqual(len(self.fake.messages),1)
        self.assertEqual(self.rows()[0]['Kind'],2)
        self.assertFalse(self.client.request('notifications/settings')[1]['enabled'])
        self.assertEqual(self.client.request('notifications/settings')[1]['lastDelivery']['state'],'Sent')

    def test_disconnect_cancels_test_message(self):
        self.bind()
        self.client.request('notifications/test','POST')
        self.client.request('notifications/connection','DELETE')
        self.tick()
        self.assertEqual(self.rows()[0]['State'],5)
        self.assertEqual(self.fake.messages,[])

    def test_test_message_requires_csrf(self):
        self.bind()
        self.assertEqual(self.client.request('notifications/test','POST',token=False)[0],400)

    def test_enabling_requires_connection(self):
        values = dict(enabled=True,timeZone='Europe/Moscow',beforeEnabled=True,beforeTime='19:00',dueEnabled=True,dueTime='09:00')
        self.assertEqual(self.client.request('notifications/settings','PUT',values)[0],409)

    def test_other_account_cannot_use_connection(self):
        self.bind()
        other = self.server.client()
        other.register('no-chat@example.test')
        other.login('no-chat@example.test')
        self.assertEqual(other.request('notifications/test','POST')[0],409)
        self.assertEqual(other.request('notifications/settings')[1]['lastDelivery'],None)


if __name__ == '__main__':
    unittest.main(verbosity=2)
