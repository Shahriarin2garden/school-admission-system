import React, { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import './style.css';
import { isExpired, isValidStudentId, remainingTime } from './appLogic';

const API = import.meta.env.VITE_API_URL;

async function call(path, options = {}) {
  let response;
  try {
    response = await fetch(API + path, options);
  } catch {
    throw new Error('Unable to reach the server. Please try again.');
  }
  let body = null;
  try {
    body = await response.json();
  } catch {
    body = null;
  }
  if (!response.ok) throw new Error(body?.error || 'Server unavailable');
  return body;
}

function App() {
  const [student, setStudent] = useState(localStorage.student || '');
  const [classes, setClasses] = useState([]);
  const [holds, setHolds] = useState([]);
  const [name, setName] = useState('');
  const [seats, setSeats] = useState(30);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState('');
  const [now, setNow] = useState(() => new Date());
  const validStudent = isValidStudentId(student);
  const headers = { 'Content-Type': 'application/json', 'X-Student-Id': student };

  const refresh = async () => {
    try {
      setClasses(await call('/classes'));
      if (validStudent) setHolds(await call('/me/holds', { headers }));
    } catch (e) {
      setError(e.message);
    }
  };

  useEffect(() => {
    refresh();
    const poll = setInterval(refresh, 3000);
    const tick = setInterval(() => setNow(new Date()), 1000);
    return () => {
      clearInterval(poll);
      clearInterval(tick);
    };
  }, [student]);

  const act = async (key, job) => {
    setBusy(key);
    try {
      await job();
      await refresh();
      setError('');
    } catch (e) {
      setError(e.message);
    } finally {
      setBusy('');
    }
  };

  const apply = id => act(`apply-${id}`, () => call(`/classes/${id}/holds`, { method: 'POST', headers }));
  const pay = id => act(`pay-${id}`, () => call(`/holds/${id}/confirm`, { method: 'POST', headers }));
  const cancel = id => act(`cancel-${id}`, () => call(`/holds/${id}`, { method: 'DELETE', headers }));
  const create = e => {
    e.preventDefault();
    act('create-class', async () => {
      await call('/classes', { method: 'POST', headers, body: JSON.stringify({ name, totalSeats: Number(seats) }) });
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
            value={student}
            inputMode="numeric"
            maxLength={17}
            onChange={e => { setStudent(e.target.value); localStorage.student = e.target.value; }}
            placeholder="20152691234567890"
          />
        </label>
      </header>

      {error && (
        <div className="error">
          <span>{error}</span>
          <button onClick={() => setError('')} aria-label="Dismiss">×</button>
        </div>
      )}
      {!validStudent && student && (
        <div className="error" role="alert">Enter a valid 17-digit student number.</div>
      )}

      <section>
        <h2>Admission classes</h2>
        <div className="grid">
          {classes.map(c => (
            <article key={c.id}>
              <h3>{c.name}</h3>
              <div className="numbers">
                <b>{c.availableSeats}<small>available</small></b>
                <span>{c.heldSeats} held</span>
                <span>{c.confirmedSeats} confirmed</span>
                <span>{c.totalSeats} total</span>
              </div>
              <button
                disabled={!validStudent || !c.availableSeats || busy !== ''}
                onClick={() => apply(c.id)}
              >
                {busy === `apply-${c.id}` ? 'Applying…' : c.availableSeats ? 'Apply' : 'Full'}
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
            {holds.map(h => {
              const expired = h.status === 'HELD' && isExpired(h.expiresAt, now);
              const left = remainingTime(h.expiresAt, now);
              return (
                <article key={h.holdId} className={expired ? 'flagged' : undefined}>
                  <div>
                    <b>{h.className}</b>
                    <small>Class {h.classId}</small>
                    <small>Hold {h.holdId}</small>
                  </div>
                  <strong className={expired ? 'expired' : h.status}>
                    {expired ? 'EXPIRED' : h.status}
                  </strong>
                  {h.status === 'HELD' && (
                    <small>
                      {expired ? 'Expired' : left ? `Expires in ${left}` : null}
                      {' · Expires '}
                      {new Date(h.expiresAt).toLocaleString()}
                    </small>
                  )}
                  {h.status === 'HELD' && (
                    <div className="actions">
                      <button disabled={busy !== '' || expired} onClick={() => pay(h.holdId)}>
                        {busy === `pay-${h.holdId}` ? 'Paying…' : 'Pay admission fee'}
                      </button>
                      <button className="secondary" disabled={busy !== '' || expired} onClick={() => cancel(h.holdId)}>
                        {busy === `cancel-${h.holdId}` ? 'Cancelling…' : 'Cancel'}
                      </button>
                    </div>
                  )}
                </article>
              );
            })}
          </div>
        ) : (
          <p className="muted">
            {validStudent ? 'No applications yet.' : 'Enter a valid 17-digit student number to apply and view applications.'}
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
            onChange={e => setName(e.target.value)}
            placeholder="Class 6 - Morning Shift"
          />
          <input
            type="number"
            min="1"
            aria-label="Total seats"
            value={seats}
            onChange={e => setSeats(e.target.value)}
          />
          <button disabled={busy === 'create-class'}>
            {busy === 'create-class' ? 'Creating…' : 'Create class'}
          </button>
        </form>
      </section>
    </main>
  );
}

createRoot(document.getElementById('root')).render(<App />);
