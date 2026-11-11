export function isValidStudentId(value) {
  return typeof value === 'string' && /^\d{17}$/.test(value);
}

export function isExpired(expiresAt, now = new Date()) {
  const time = new Date(expiresAt).getTime();
  if (!Number.isFinite(time)) return true;
  return time <= now.getTime();
}

export function remainingTime(expiresAt, now = new Date()) {
  const ms = new Date(expiresAt).getTime() - now.getTime();
  if (!Number.isFinite(ms) || ms <= 0) return null;
  const totalSeconds = Math.floor(ms / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  if (hours > 0) return `${hours}h ${minutes}m`;
  if (minutes > 0) return `${minutes}m ${seconds}s`;
  return `${seconds}s`;
}

export function effectiveStatus(hold, now = new Date()) {
  if (!hold) return '';
  if (hold.status === 'HELD' && isExpired(hold.expiresAt, now)) return 'EXPIRED';
  return hold.status;
}
