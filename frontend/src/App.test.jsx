import React from 'react';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from './App';

const STUDENT = '20152691234567890';
const OTHER_STUDENT = '20152691234567891';

const futureIso = (offsetMs = 10 * 60 * 1000) => new Date(Date.now() + offsetMs).toISOString();
const pastIso = () => new Date(Date.now() - 60 * 1000).toISOString();

function classItem(overrides = {}) {
  return {
    id: 'c1',
    name: 'Class 6 Morning',
    totalSeats: 3,
    heldSeats: 1,
    confirmedSeats: 0,
    availableSeats: 2,
    ...overrides
  };
}

function holdItem(overrides = {}) {
  return {
    holdId: 'h_1',
    classId: 'c1',
    className: 'Class 6 Morning',
    studentId: STUDENT,
    status: 'HELD',
    expiresAt: futureIso(),
    ...overrides
  };
}

function jsonResponse(body, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    text: async () => (body === undefined ? '' : JSON.stringify(body)),
    json: async () => body
  };
}

function makeApi({
  classes = [],
  holds = [],
  failClasses = false,
  applyError = null,
  createError = null
} = {}) {
  const state = { classes: [...classes], holds: [...holds], seq: 0 };
  const fetchMock = vi.fn(async (url, options = {}) => {
    const method = options.method || 'GET';
    const studentId = options.headers?.['X-Student-Id'];

    if (failClasses && url.endsWith('/classes') && method === 'GET') {
      throw new Error('network unavailable');
    }

    if (url.endsWith('/classes') && method === 'GET') {
      return jsonResponse(state.classes);
    }

    if (url.endsWith('/me/holds') && method === 'GET') {
      return jsonResponse(state.holds.filter(hold => hold.studentId === studentId));
    }

    let match = url.match(/\/classes\/([^/]+)\/holds$/);
    if (match && method === 'POST') {
      if (applyError) {
        return jsonResponse({ error: applyError }, 409);
      }
      state.seq += 1;
      const parent = state.classes.find(item => item.id === match[1]);
      const hold = holdItem({
        holdId: `h_${state.seq}`,
        classId: match[1],
        className: parent?.name || 'Class',
        studentId
      });
      state.holds.push(hold);
      return jsonResponse(hold, 201);
    }

    match = url.match(/\/holds\/([^/]+)\/confirm$/);
    if (match && method === 'POST') {
      const hold = state.holds.find(item => item.holdId === match[1]);
      if (hold) hold.status = 'CONFIRMED';
      return jsonResponse({ holdId: match[1], status: 'CONFIRMED' });
    }

    match = url.match(/\/holds\/([^/]+)$/);
    if (match && method === 'DELETE') {
      const hold = state.holds.find(item => item.holdId === match[1]);
      if (hold) hold.status = 'CANCELLED';
      return jsonResponse(undefined, 204);
    }

    if (url.endsWith('/classes') && method === 'POST') {
      if (createError) {
        return jsonResponse({ error: createError }, 400);
      }
      const body = JSON.parse(options.body);
      state.seq += 1;
      const created = {
        id: `c_${state.seq}`,
        name: body.name,
        totalSeats: body.totalSeats,
        heldSeats: 0,
        confirmedSeats: 0,
        availableSeats: body.totalSeats
      };
      state.classes.push(created);
      return jsonResponse(created, 201);
    }

    return jsonResponse({ error: 'not found' }, 404);
  });

  return { fetchMock, state };
}

async function renderWithStudent(api, student = STUDENT) {
  localStorage.setItem('studentId', student);
  vi.stubGlobal('fetch', api.fetchMock);
  return render(<App pollIntervalMs={60000} clockIntervalMs={60000} />);
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  localStorage.clear();
});

describe('Admission portal', () => {
  it('loads and displays classes with live seat counts', async () => {
    const api = makeApi({ classes: [classItem()] });
    await renderWithStudent(api, '');

    expect(await screen.findByText('Class 6 Morning')).toBeInTheDocument();
    expect(screen.getByText('1 held')).toBeInTheDocument();
    expect(screen.getByText('0 confirmed')).toBeInTheDocument();
    expect(screen.getByText('3 total')).toBeInTheDocument();
  });

  it('shows validation feedback for an invalid student identifier', async () => {
    const api = makeApi();
    await renderWithStudent(api, '');

    const input = screen.getByLabelText('Student birth registration number');
    await userEvent.type(input, '12345');

    expect(await screen.findByText('Enter a valid 17-digit student number.')).toBeInTheDocument();
    expect(api.fetchMock.mock.calls.some(([url]) => url.endsWith('/me/holds'))).toBe(false);
  });

  it('creates an application and shows it in My applications', async () => {
    const api = makeApi({ classes: [classItem({ id: 'c1', name: 'Class 6 Morning' })] });
    await renderWithStudent(api);

    await userEvent.click(await screen.findByRole('button', { name: 'Apply' }));

    expect(await screen.findByText('Hold h_1')).toBeInTheDocument();
    const call = api.fetchMock.mock.calls.find(
      ([url, options]) => url.endsWith('/classes/c1/holds') && options.method === 'POST'
    );
    expect(call[1].headers['X-Student-Id']).toBe(STUDENT);
  });

  it('renders hold ids, class ids, statuses and absolute expiration times', async () => {
    const api = makeApi({ holds: [holdItem()], classes: [] });
    await renderWithStudent(api);

    expect(await screen.findByText('Hold h_1')).toBeInTheDocument();
    expect(screen.getByText('Class c1')).toBeInTheDocument();
    expect(screen.getByText('HELD')).toBeInTheDocument();
    expect(screen.getByText(/Expires in/)).toBeInTheDocument();
    expect(screen.getByText(/Expires /)).toBeInTheDocument();
  });

  it('clears the previous student applications when identity changes', async () => {
    const api = makeApi({
      classes: [],
      holds: [
        holdItem({ holdId: 'h_old', studentId: STUDENT }),
        holdItem({ holdId: 'h_new', studentId: OTHER_STUDENT, className: 'Class 7' })
      ]
    });
    await renderWithStudent(api, STUDENT);

    expect(await screen.findByText('Hold h_old')).toBeInTheDocument();

    const input = screen.getByLabelText('Student birth registration number');
    await userEvent.clear(input);
    await userEvent.type(input, OTHER_STUDENT);

    await waitFor(() => expect(screen.queryByText('Hold h_old')).not.toBeInTheDocument());
    expect(await screen.findByText('Hold h_new')).toBeInTheDocument();
  });

  it('simulates payment through the confirmation endpoint', async () => {
    const api = makeApi({ holds: [holdItem()], classes: [] });
    await renderWithStudent(api);

    await userEvent.click(await screen.findByRole('button', { name: 'Pay admission fee (simulated)' }));

    await waitFor(() => expect(screen.getByText('CONFIRMED')).toBeInTheDocument());
    const confirmCall = api.fetchMock.mock.calls.find(
      ([url, options]) => url.endsWith('/holds/h_1/confirm') && options.method === 'POST'
    );
    expect(confirmCall[1].headers['X-Student-Id']).toBe(STUDENT);
  });

  it('cancels a held application', async () => {
    const api = makeApi({ holds: [holdItem()], classes: [] });
    await renderWithStudent(api);

    await userEvent.click(await screen.findByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.getByText('CANCELLED')).toBeInTheDocument());
    expect(
      api.fetchMock.mock.calls.some(
        ([url, options]) => url.endsWith('/holds/h_1') && options.method === 'DELETE'
      )
    ).toBe(true);
  });

  it('presents an overdue hold as EXPIRED and disables its actions', async () => {
    const api = makeApi({ holds: [holdItem({ expiresAt: pastIso() })], classes: [] });
    await renderWithStudent(api);

    expect(await screen.findByText('EXPIRED')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Pay admission fee (simulated)' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();
  });

  it('reports an unreachable server without rendering a blank page', async () => {
    const api = makeApi({ failClasses: true });
    await renderWithStudent(api, '');

    expect(
      await screen.findByText('Unable to reach the server. Please try again.')
    ).toBeInTheDocument();
    expect(screen.getByText('No admission classes yet.')).toBeInTheDocument();
  });

  it('shows a no-seat conflict returned by the server', async () => {
    const api = makeApi({
      classes: [classItem()],
      applyError: 'No seats available.'
    });
    await renderWithStudent(api);

    await userEvent.click(await screen.findByRole('button', { name: 'Apply' }));

    expect(await screen.findByText('No seats available.')).toBeInTheDocument();
  });

  it('shows validation errors returned when creating a class', async () => {
    const api = makeApi({ createError: 'Class name is required.' });
    await renderWithStudent(api, '');

    const nameInput = screen.getByLabelText('Class name');
    await userEvent.type(nameInput, 'A');
    await userEvent.click(screen.getByRole('button', { name: 'Create class' }));

    expect(await screen.findByText('Class name is required.')).toBeInTheDocument();
  });

  it('stops polling after unmount', async () => {
    vi.useFakeTimers();
    const api = makeApi({ classes: [classItem()] });
    vi.stubGlobal('fetch', api.fetchMock);

    const { unmount } = render(<App pollIntervalMs={1000} clockIntervalMs={1000} />);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    const initialCalls = api.fetchMock.mock.calls.length;

    await act(async () => {
      await vi.advanceTimersByTimeAsync(3000);
    });
    const polledCalls = api.fetchMock.mock.calls.length;
    expect(polledCalls).toBeGreaterThan(initialCalls);

    unmount();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(5000);
    });

    expect(api.fetchMock.mock.calls.length).toBe(polledCalls);
  });
});
