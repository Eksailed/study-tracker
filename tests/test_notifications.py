from contextlib import closing
from pathlib import Path
import sqlite3
import unittest
from support import Server


class NotificationTests(unittest.TestCase):
    def setUp(self):
        self.server = Server().start()
        self.addCleanup(self.server.close)
        self.client = self.server.client()
        self.client.register('notify@example.test')
        self.client.login('notify@example.test')

    def test_notification_defaults(self):
        code,settings = self.client.request('notifications/settings')
        self.assertEqual(code,200)
        self.assertFalse(settings['enabled'])
        self.assertFalse(settings['configured'])
        self.assertEqual(settings['timeZone'],'Europe/Moscow')
        self.assertEqual((settings['beforeTime'],settings['dueTime']),('19:00','09:00'))

    def test_restart_without_token_marks_inflight_unknown(self):
        owner = self.client.request('auth/me')[1]['id']
        self.server.stop()
        with closing(sqlite3.connect(Path(self.server.temp.name)/'tracker.db')) as db, db:
            db.execute('''INSERT INTO ReminderDeliveries(OwnerId,Kind,ScheduledAt,NextAttemptAt,ExpiresAt,
                          BindingVersion,State,Attempts) VALUES (?,2,0,0,0,0,1,1)''',(owner,))
        self.server.start()
        self.assertEqual(self.client.request('notifications/settings')[1]['lastDelivery']['state'],'Unknown')

    def test_settings_are_private(self):
        settings = dict(enabled=False,timeZone='Asia/Yekaterinburg',beforeEnabled=True,beforeTime='18:30',dueEnabled=False,dueTime='08:00')
        self.assertEqual(self.client.request('notifications/settings','PUT',settings)[0],204)
        other = self.server.client()
        other.register('other-notify@example.test')
        other.login('other-notify@example.test')
        self.assertEqual(other.request('notifications/settings')[1]['timeZone'],'Europe/Moscow')
        self.assertEqual(self.client.request('notifications/settings')[1]['timeZone'],'Asia/Yekaterinburg')

    def test_notification_settings_require_csrf(self):
        self.assertEqual(self.client.request('notifications/settings','PUT',{},token=False)[0],400)
        self.assertEqual(self.server.client().request('notifications/settings')[0],401)

    def test_invalid_timezone_is_rejected(self):
        settings = dict(enabled=False,timeZone='Unknown/Zone',beforeEnabled=True,beforeTime='19:00',dueEnabled=True,dueTime='09:00')
        self.assertEqual(self.client.request('notifications/settings','PUT',settings)[0],400)
        self.assertEqual(self.client.request('notifications/settings','PUT',settings|{'timeZone':'Europe/Moscow','dueTime':'25:00'})[0],400)

    def test_duplicate_reminder_is_rejected(self):
        owner = self.client.request('auth/me')[1]['id']
        with closing(sqlite3.connect(Path(self.server.temp.name)/'tracker.db')) as db, db:
            values = (owner,123,'2026-12-12',0,0,0,0,0,0,0)
            sql = '''INSERT INTO ReminderDeliveries(OwnerId,AssignmentId,DueDate,Kind,ScheduledAt,NextAttemptAt,
                     ExpiresAt,BindingVersion,State,Attempts) VALUES (?,?,?,?,?,?,?,?,?,?)'''
            db.execute(sql,values)
            with self.assertRaises(sqlite3.IntegrityError):
                db.execute(sql,values)

    def test_notification_migration_preserves_accounts(self):
        subject = self.client.request('subjects','POST',dict(name='Сохранить',color='blue'))[1]
        task = self.client.request('assignments','POST',dict(title='Сохранить задание',subjectId=subject['id'],dueDate='2026-12-12',status='Todo'))[1]
        self.server.stop()
        with closing(sqlite3.connect(Path(self.server.temp.name)/'tracker.db')) as db, db:
            for table in ('ReminderDeliveries','TelegramLinkCodes','TelegramConnections','NotificationSettings','TelegramUpdateStates'):
                db.execute(f'DROP TABLE {table}')
            db.execute("DELETE FROM __EFMigrationsHistory WHERE MigrationId >= (SELECT MigrationId FROM __EFMigrationsHistory WHERE MigrationId LIKE '%_Notifications')")
        self.server.start()
        self.assertEqual(self.client.request('auth/me')[0],200)
        self.assertEqual(self.client.request('assignments')[1][0]['id'],task['id'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
