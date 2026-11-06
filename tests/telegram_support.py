from contextlib import closing
from pathlib import Path
import sqlite3
import time
from urllib.parse import urlparse, parse_qs
from support import Server
from fake_telegram import FakeTelegram, TOKEN


class TelegramFixture:
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

    def bind(self):
        self.fake.update(self.code())
        self.poll()
        self.assertTrue(self.client.request('notifications/settings')[1]['connected'])
        self.fake.messages.clear()

    def settings(self, **changes):
        data = dict(enabled=True,timeZone='Europe/Moscow',beforeEnabled=True,beforeTime='19:00',dueEnabled=True,dueTime='09:00')
        data.update(changes)
        self.assertEqual(self.client.request('notifications/settings','PUT',data)[0],204)

    def task(self):
        subject = self.client.request('subjects','POST',dict(name='Предмет',color='teal'))[1]
        code,task = self.client.request('assignments','POST',dict(title='Название задания',description='Не передавать описание',
                                       subjectId=subject['id'],dueDate='2026-11-12',status='Todo'))
        self.assertEqual(code,201)
        return task

    def advance(self, value):
        self.server.stop()
        self.server.env['Testing__UtcNow'] = value
        self.server.start()
        self.wait_ready()

    def plan(self):
        result = self.server.command('--notifications-plan')
        self.assertEqual(result.returncode,0,result.stderr)

    def rows(self):
        with closing(sqlite3.connect(Path(self.server.temp.name)/'tracker.db')) as db:
            db.row_factory = sqlite3.Row
            return [dict(row) for row in db.execute('SELECT * FROM ReminderDeliveries ORDER BY Id')]
