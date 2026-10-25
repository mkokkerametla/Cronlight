import { DatabaseSync } from 'node:sqlite'

// Kept apart from helpers.ts so that only the spec using it needs node:sqlite (Node 22.13+)

/**
 * Writes a job straight into the sample's Quartz tables with a type this app does not have, as another
 * app sharing the scheduler would. `database` is the worker's sample host database. Returns a function that
 * removes the job again.
 */
export function insertForeignJob(database: string, group: string, name: string) {
  const run = (sql: string) => {
    const db = new DatabaseSync(database)
    try {
      db.exec('PRAGMA busy_timeout = 5000')
      db.prepare(sql).run(group, name)
    } finally {
      db.close()
    }
  }
  run(
    "INSERT INTO QRTZ_JOB_DETAILS VALUES ('SampleHost', ?2, ?1, NULL, 'OtherApp.Jobs.ReportJob, OtherApp', 1, 0, 0, 0, NULL)"
  )
  return () => run("DELETE FROM QRTZ_JOB_DETAILS WHERE SCHED_NAME = 'SampleHost' AND JOB_GROUP = ?1 AND JOB_NAME = ?2")
}
