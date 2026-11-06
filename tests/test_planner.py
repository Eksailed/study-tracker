from datetime import datetime, timezone
import unittest
from telegram_support import TelegramFixture


class PlannerTests(TelegramFixture,unittest.TestCase):
    def prepare(self):
        self.bind()
        self.settings()
        return self.task()

    def test_moscow_reminder_times(self):
        self.prepare()
        self.advance('2026-11-11T16:00:00Z')
        self.plan()
        row = self.rows()[0]
        self.assertEqual(row['State'],0)
        self.assertEqual(datetime.fromtimestamp(row['ScheduledAt']/1000,timezone.utc).isoformat(),'2026-11-11T16:00:00+00:00')
        self.advance('2026-11-12T06:00:00Z')
        self.plan()
        due = next(r for r in self.rows() if r['Kind'] == 1)
        self.assertEqual(datetime.fromtimestamp(due['ScheduledAt']/1000,timezone.utc).isoformat(),'2026-11-12T06:00:00+00:00')

    def test_restart_does_not_duplicate_reminder(self):
        self.prepare()
        self.advance('2026-11-11T16:00:00Z')
        self.plan()
        self.advance('2026-11-11T16:01:00Z')
        self.plan()
        self.assertEqual(len(self.rows()),1)

    def test_completed_assignment_is_not_notified(self):
        task = self.prepare()
        self.client.request(f'assignments/{task["id"]}','PUT',task|{'status':'Done'})
        self.advance('2026-11-11T16:00:00Z')
        self.plan()
        self.assertEqual(self.rows(),[])

    def test_changed_due_date_cancels_old_pending_reminder(self):
        task = self.prepare()
        self.advance('2026-11-11T16:00:00Z')
        self.plan()
        self.client.request(f'assignments/{task["id"]}','PUT',task|{'dueDate':'2026-11-15'})
        self.plan()
        self.assertEqual(self.rows()[0]['State'],5)

    def test_disconnected_chat_cancels_pending_reminders(self):
        self.prepare()
        self.advance('2026-11-11T16:00:00Z')
        self.plan()
        self.client.request('notifications/connection','DELETE')
        self.assertEqual(self.rows()[0]['State'],5)

    def test_missed_window_is_skipped(self):
        self.prepare()
        self.advance('2026-11-11T16:14:59Z')
        self.plan()
        self.assertEqual(self.rows()[0]['State'],0)
        self.advance('2026-11-11T16:15:00Z')
        self.plan()
        self.assertEqual(self.rows()[0]['State'],6)

    def test_changed_time_replans_unsent_reminder(self):
        self.prepare()
        self.advance('2026-11-11T16:00:00Z')
        self.plan()
        self.settings(beforeTime='19:05')
        self.plan()
        self.assertEqual(self.rows()[0]['State'],5)
        self.advance('2026-11-11T16:05:00Z')
        self.plan()
        self.assertEqual(self.rows()[0]['State'],0)


if __name__ == '__main__':
    unittest.main(verbosity=2)
