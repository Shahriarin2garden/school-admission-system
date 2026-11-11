import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { effectiveStatus, isExpired, isValidStudentId, remainingTime } from './appLogic';

const API = (import.meta.env.VITE_API_URL || 'http://localhost:8080').replace(/\/$/, '');

const systemNow = () => new Date();

export async function request(path, options = {}) {
  let response;
  try {
    response = await fetch(API + path, options);
  } catch (error) {
    if (error.name === 'AbortError') throw error;
    throw new Error('Unable to reach the server. Please try again.');
  }

  let body = null;
  const text = await response.text();
  if (text) {
    try {
      body = JSON.parse(text);
    } catch {
      body = null;
    }
  }

  if (!response.ok) {
    const fallback = response.status >= 500
      ? 'The server is temporarily unavailable. Please try again.'
      : 'The request could not be completed.';
    const error = new Error(body?.error || fallback);
    error.status = response.status;
    throw error;
  }

  return body;
}

export default function App({
  pollIntervalMs = 3000,
  clockIntervalMs = 1000,
  nowProvider = systemNow
}) {
  const [student, setStudent] = useState(() => localStorage.getItem('studentId') || '');
  const [classes, setClasses] = useState([]);
  const [holds, setHolds] = useState([]);
  const [name, setName] = useState('');
  const [seats, setSeats] = useState(30);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState('');
  const [now, setNow] = useState(() => nowProvider());

  const currentStudentRef = useRef(student);
  const holdsVersionRef = useRef(0);
  const classesVersionRef = useRef(0);
  const holdsInFlightRef = useRef(null);
  const classesInFlightRef = useRef(null);
  const busyRef = useRef(false);

  const validStudent = isValidStudentId(student);

  const studentHeaders = useMemo(
    () => ({ 'Content-Type': 'application/json', 'X-Student-Id': student }),
    [student]
  );

  const loadClasses = useCallback(async (signal, version) => {
    if (version !== classesVersionRef.current || classesInFlightRef.current === version) {
      return;
    }

    classesInFlightRef.current = version;
    try {
      const data = await request('/classes', { signal });
      if (!signal?.aborted && version === classesVersionRef.current) {
        setClasses(Array.isArray(data) ? data : []);
      }
    } catch (err) {
      if (err.name !== 'AbortError' && version === classesVersionRef.current) {
        setError(err.message);
      }
    } finally {
      if (classesInFlightRef.current === version) {
        classesInFlightRef.current = null;
      }
    }
  }, []);

  const loadHolds = useCallback(async (signal, version) => {
    const studentAtRequest = currentStudentRef.current;
    if (!isValidStudentId(studentAtRequest) || version !== holdsVersionRef.current) {
      return;
    }
    if (holdsInFlightRef.current === version) {
      return;
    }

    holdsInFlightRef.current = version;
    try {
      const data = await request('/me/holds', {
        headers: { 'X-Student-Id': studentAtRequest },
        signal
      });
      if (
        !signal?.aborted &&
        version === holdsVersionRef.current &&
        currentStudentRef.current === studentAtRequest
      ) {
        setHolds(Array.isArray(data) ? data : []);
      }
    } catch (err) {
      if (err.name !== 'AbortError' && version === holdsVersionRef.current) {
        setError(err.message);
      }
    } finally {
      if (holdsInFlightRef.current === version) {
        holdsInFlightRef.current = null;
      }
    }
  }, []);

  // Persist identity, but note that the header is identification, not
  // authentication (documented in ASSUMPTIONS.md).
  useEffect(() => {
    currentStudentRef.current = student;
    localStorage.setItem('studentId', student);
  }, [student]);

  // Class availability refreshes independently of the selected student.
  useEffect(() => {
    classesVersionRef.current += 1;
    const controller = new AbortController();
    loadClasses(controller.signal, classesVersionRef.current);
    const interval = setInterval(
      () => loadClasses(controller.signal, classesVersionRef.current),
      pollIntervalMs
    );
    return () => {
      controller.abort();
      clearInterval(interval);
    };
  }, [loadClasses, pollIntervalMs]);

  // Changing identity immediately drops the previous student's applications.
  // The generation counter prevents a late response from the previous student
  // from repopulating the list.
  useEffect(() => {
    setHolds([]);
    setError('');
    holdsVersionRef.current += 1;
    holdsInFlightRef.current = null;

    if (!isValidStudentId(student)) {
      return undefined;
    }

    const controller = new AbortController();
    loadHolds(controller.signal, holdsVersionRef.current);
    const interval = setInterval(
      () => loadHolds(controller.signal, holdsVersionRef.current),
      pollIntervalMs
    );
    return () => {
      controller.abort();
      clearInterval(interval);
    };
  }, [student, loadHolds, pollIntervalMs]);

  useEffect(() => {
    setNow(nowProvider());
    const interval = setInterval(() => setNow(nowProvider()), clockIntervalMs);
    return () => clearInterval(interval);
  }, [clockIntervalMs, nowProvider]);

  const refreshAll = useCallback(async () => {
    classesVersionRef.current += 1;
    holdsVersionRef.current += 1;
    holdsInFlightRef.current = null;
    classesInFlightRef.current = null;
    const controller = new AbortController();
    await Promise.all([
      loadClasses(controller.signal, classesVersionRef.current),
      loadHolds(controller.signal, holdsVersionRef.current)
    ]);
  }, [loadClasses, loadHolds]);

  const runAction = async (key, job) => {
    if (busyRef.current) return;
    busyRef.current = true;
    setBusy(key);
    setError('');
    try {
      await job();
      await refreshAll();
    } catch (err) {
      if (err.name !== 'AbortError') {
        setError(err.message);
      }
    } finally {
      busyRef.current = false;
      setBusy('');
    }
  };

  const apply = classId =>
    runAction(`apply-${classId}`, () =>
      request(`/classes/${classId}/holds`, {
        method: 'POST',
        headers: studentHeaders,
        body: '{}'
      })
    );

  const pay = holdId =>
    runAction(`pay-${holdId}`, () =>
      request(`/holds/${holdId}/confirm`, { method: 'POST', headers: studentHeaders })
    );

  const cancel = holdId =>
    runAction(`cancel-${holdId}`, () =>
      request(`/holds/${holdId}`, { method: 'DELETE', headers: studentHeaders })
    );

  const create = event => {
    event.preventDefault();
    runAction('create-class', async () => {
      await request('/classes', {
        method: 'POST',
        headers: studentHeaders,
        body: JSON.stringify({ name, totalSeats: Number(seats) })
      });
      setName('');
    });
  };

  return (
    <main>
      <header>
        <div className="brand">
          <h1>Admission Portal</h1>
          <p>First come, first served · live seat availability</p>
        </div>
        <label>
          Student birth registration (17 digits)
          <input
            aria-label="Student birth registration number"
            value={student}
            inputMode="numeric"
            maxLength={17}
            onChange={event => setStudent(event.target.value)}
            placeholder="20152691234567890"
            autoComplete="off"
          />
        </label>
        <button
          type="button"
          className="secondary"
          aria-label="Clear student identity"
          onClick={() => setStudent('')}
          disabled={!student}
        >
          Clear
        </button>
      </header>

      {error && (
        <div className="error" role="alert">
          <span>{error}</span>
          <button onClick={() => setError('')} aria-label="Dismiss error">×</button>
        </div>
      )}
      {!validStudent && student && (
        <div className="error" role="alert">Enter a valid 17-digit student number.</div>
      )}

      <section>
        <h2>Admission classes</h2>
        <div className="grid">
          {classes.map(item => (
            <article key={item.id}>
              <h3>{item.name}</h3>
              <div className="numbers">
                <b>{item.availableSeats}<small>available</small></b>
                <span>{item.heldSeats} held</span>
                <span>{item.confirmedSeats} confirmed</span>
                <span>{item.totalSeats} total</span>
              </div>
              <button
                disabled={!validStudent || !item.availableSeats || busy !== ''}
                onClick={() => apply(item.id)}
              >
                {busy === `apply-${item.id}` ? 'Applying…' : item.availableSeats ? 'Apply' : 'Full'}
              </button>
            </article>
          ))}
          {!classes.length && <p className="muted">No admission classes yet.</p>}
        </div>
      </section>

      <section>
        <h2>My applications</h2>
        {holds.length ? (
          <div className="holds">
            {holds.map(hold => {
              const status = effectiveStatus(hold, now);
              const expired = status === 'EXPIRED';
              const left = remainingTime(hold.expiresAt, now);
              const canAct = status === 'HELD';
              return (
                <article key={hold.holdId} className={expired ? 'flagged' : undefined}>
                  <div>
                    <b>{hold.className}</b>
                    <small>Class {hold.classId}</small>
                    <small>Hold {hold.holdId}</small>
                  </div>
                  <strong className={expired ? 'expired' : status}>{status}</strong>
                  {status === 'HELD' && (
                    <small>
                      {left ? `Expires in ${left}` : 'Expiring…'}
                      {' · Expires '}
                      {new Date(hold.expiresAt).toLocaleString()}
                    </small>
                  )}
                  {hold.status === 'HELD' && (
                    <div className="actions">
                      <button
                        disabled={busy !== '' || !canAct || isExpired(hold.expiresAt, now)}
                        onClick={() => pay(hold.holdId)}
                      >
                        {busy === `pay-${hold.holdId}` ? 'Paying…' : 'Pay admission fee (simulated)'}
                      </button>
                      <button
                        className="secondary"
                        disabled={busy !== '' || !canAct || isExpired(hold.expiresAt, now)}
                        onClick={() => cancel(hold.holdId)}
                      >
                        {busy === `cancel-${hold.holdId}` ? 'Cancelling…' : 'Cancel'}
                      </button>
                    </div>
                  )}
                </article>
              );
            })}
          </div>
        ) : (
          <p className="muted">
            {validStudent
              ? 'No applications yet.'
              : 'Enter a valid 17-digit student number to apply and view applications.'}
          </p>
        )}
      </section>

      <section>
        <h2>Administration · create class</h2>
        <form onSubmit={create}>
          <input
            required
            aria-label="Class name"
            value={name}
            onChange={event => setName(event.target.value)}
            placeholder="Class 6 - Morning Shift"
          />
          <input
            type="number"
            min="1"
            aria-label="Total seats"
            value={seats}
            onChange={event => setSeats(event.target.value)}
          />
          <button disabled={busy === 'create-class'}>
            {busy === 'create-class' ? 'Creating…' : 'Create class'}
          </button>
        </form>
      </section>
    </main>
  );
}
