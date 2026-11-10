export function isValidStudentId(value) {
  return /^\d{17}$/.test(value);
}

export function isExpired(expiresAt, now = new Date()) {
  return new Date(expiresAt) <= now;
}

export function remainingTime(expiresAt, now = new Date()) {
  const ms = new Date(expiresAt) - now;
  if (ms <= 0) return null;
  const totalSeconds = Math.floor(ms / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  if (hours > 0) return `${hours}h ${minutes}m`;
  if (minutes > 0) return `${minutes}m ${seconds}s`;
  return `${seconds}s`;
}
